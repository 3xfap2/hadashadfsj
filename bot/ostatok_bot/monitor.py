"""Фоновый опрос стоп-листа, партий и сигналов и уведомления вошедшим чатам.

Ресторанов несколько, и каждый токен видит только свой. Поэтому опрос идёт отдельно по каждому
ресторану, где есть вошедшие чаты, токеном одного из этих чатов, а события уходят только им.
Партии видят кладовщик и кухня; официант получает только изменения стоп-листа.

Сроки партий и стоп-лист бот сравнивает сам. Из сигналов сервера (/api/alerts) берутся только остальные
виды: поставка ждёт подтверждения и виды, которых бот ещё не знает, — их текст показывается как есть.
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Callable, Literal

from aiogram import Bot
from aiogram.exceptions import TelegramForbiddenError, TelegramRetryAfter
from aiogram.types import InlineKeyboardMarkup

from .api import ApiClient, ApiError, ApiUnavailable, Unauthorized
from .commands import show_guest_commands
from .fmt import fmt_hours_left, fmt_qty
from .keyboards import write_off_keyboard
from .storage import Session, Storage, utcnow
from .texts import SESSION_EXPIRED, stop_reason

log = logging.getLogger(__name__)

STATE_PREFIX = "restaurant:"
# Состояние старше этого считается устаревшим: пока в ресторане никто не был в боте, события не копились.
MIN_STALE_AFTER = timedelta(minutes=10)
BATCH_SECTIONS = ("stock", "kitchen")
# Эти виды сигналов бот уже присылает по своему сравнению стоп-листа и партий.
DIFF_ALERT_KINDS = {"BatchExpiring", "BatchExpired", "DishStopped", "DishBack"}
ALERTS_TAKE = 30
# Сколько новых сигналов присылать за раз, например после простоя бота.
MAX_ALERTS = 5
# Дольше этого по просьбе Telegram (429) не ждём: опрос остальных ресторанов важнее.
MAX_RETRY_AFTER = 30

EventKind = Literal["dish_stop", "dish_resumed", "batch_expiring", "batch_expired", "supply_waiting", "alert"]


@dataclass(frozen=True)
class Event:
    kind: EventKind
    text: str
    batch: dict | None = None


def snapshot(stop_list: list[dict], batches: list[dict]) -> dict:
    return {
        "dishes": {d["dishId"]: d["status"] for d in stop_list},
        "batches": {b["id"]: b["status"] for b in batches},
    }


def diff(previous: dict | None, stop_list: list[dict], batches: list[dict]) -> list[Event]:
    """События между прошлым и текущим состоянием. Первый запуск — без событий."""
    if previous is None:
        return []
    events: list[Event] = []
    old_dishes = previous.get("dishes", {})
    for dish in stop_list:
        was = old_dishes.get(dish["dishId"])
        now = dish["status"]
        if now == "Stop" and was != "Stop":
            reason = stop_reason(dish.get("reason"), dish.get("limitingProductName"))
            events.append(Event("dish_stop", f"В стопе: {dish['dishName']} — {reason}"))
        elif was == "Stop" and now != "Stop":
            events.append(Event("dish_resumed", f"Снова доступно: {dish['dishName']}"))

    old_batches = previous.get("batches", {})
    for batch in batches:
        was = old_batches.get(batch["id"])
        now = batch["status"]
        if now == was:
            continue
        what = f"{batch['productName']}, {fmt_qty(batch['remaining'], batch['unit'])}, поставка {batch['supplyNumber']}"
        if now == "Expired":
            events.append(Event("batch_expired", f"Просрочено: {what}", batch))
        elif now == "ExpiringSoon":
            events.append(
                Event("batch_expiring", f"Скоро истекает: {what}, {fmt_hours_left(batch['hoursLeft'])}", batch)
            )
    return events


def alert_id(alert: dict) -> int:
    value = alert.get("id") if isinstance(alert, dict) else None
    return value if isinstance(value, int) else 0


def alert_events(alerts: list[dict], last_id: int | None) -> list[Event]:
    """События по сигналам новее last_id. Без last_id (первый опрос) — без событий: старое не присылаем."""
    if last_id is None:
        return []
    # Сначала отбросить виды, которые бот считает сам, и только потом брать последние: иначе ждущую
    # поставку вытеснят пять более новых сигналов о партиях.
    fresh = sorted(
        (a for a in alerts if alert_id(a) > last_id and a.get("kind") not in DIFF_ALERT_KINDS), key=alert_id
    )[-MAX_ALERTS:]
    events = []
    for alert in fresh:
        kind = alert.get("kind")
        text = "\n".join(str(alert[k]).strip() for k in ("title", "detail") if alert.get(k)) or "Новый сигнал"
        if kind == "SupplyWaiting":
            events.append(Event("supply_waiting", f"{text}\nПринять или отклонить: /incoming"))
        else:
            events.append(Event("alert", text))
    return events


def render(events: list[Event], can_write_off: bool = True) -> tuple[str, InlineKeyboardMarkup | None]:
    """Все события одного опроса — одним сообщением; под просроченными — «Списать», если роль может списывать."""
    text = "\n".join(e.text for e in events)
    expired = [e.batch for e in events if e.kind == "batch_expired" and e.batch]
    return text, (write_off_keyboard(expired) if expired and can_write_off else None)


def visible_events(events: list[Event], session: Session) -> list[Event]:
    """Партии — тем, кто работает со складом или кухней; стоп-лист — всем."""
    sees_batches = any(session.allows(section) for section in BATCH_SECTIONS)
    return [
        e for e in events
        if (sees_batches or not e.kind.startswith("batch_")) and (e.kind != "supply_waiting" or session.allows("stock"))
    ]


class Monitor:
    def __init__(
        self,
        api: ApiClient,
        storage: Storage,
        bot: Bot,
        poll_seconds: float,
        clock: Callable[[], datetime] = utcnow,
    ):
        self._api = api
        self._storage = storage
        self._bot = bot
        self._poll_seconds = poll_seconds
        self._clock = clock
        self._stale_after = max(MIN_STALE_AFTER, timedelta(seconds=poll_seconds * 5))

    async def poll_once(self) -> dict[str, list[Event]]:
        """Опросить все рестораны с вошедшими чатами. Возвращает события по ресторанам."""
        now = self._clock()
        by_restaurant: dict[str, list[Session]] = {}
        for session in self._storage.active_sessions(now):
            if session.restaurant_id:
                by_restaurant.setdefault(session.restaurant_id, []).append(session)
        result = {}
        for restaurant_id, sessions in by_restaurant.items():
            result[restaurant_id] = []
            try:
                result[restaurant_id] = await self._poll_restaurant(restaurant_id, sessions, now)
            except ApiUnavailable as exc:
                # Сервер недоступен для всех ресторанов сразу: не ждать тот же таймаут на каждом.
                log.warning("Опрос API не удался, цикл прерван до следующего раза: %s", exc)
                break
            except Exception:
                # Сбой одного ресторана (неожиданный ответ, испорченное состояние) не останавливает остальные.
                log.exception("Опрос ресторана %s не удался", restaurant_id)
        return result

    async def _poll_restaurant(self, restaurant_id: str, sessions: list[Session], now: datetime) -> list[Event]:
        data = None
        token = None
        logged_out: set[int] = set()
        for session in sessions:
            try:
                data = (await self._api.get_stop_list(session.token), await self._api.get_batches(session.token))
                token = session.token
                break
            except Unauthorized:
                # Токен отозван: этот чат должен войти заново, повтор с ним не поможет. Пробуем токен коллеги.
                logged_out.add(session.chat_id)
                await self._log_out(session)
            except ApiUnavailable:
                raise
            except ApiError as exc:
                log.warning("Опрос ресторана %s токеном чата %s не удался: %s", restaurant_id, session.chat_id, exc)
        if data is None:
            return []

        key = STATE_PREFIX + restaurant_id
        saved = self._load_state(key)
        previous = None
        if saved and now - datetime.fromisoformat(saved["at"]) <= self._stale_after:
            previous = saved["snapshot"]
        events = diff(previous, *data)

        # Сигналы хранятся на сервере, поэтому их не теряем и после простоя: отсчёт по id, а не по времени.
        last_id = saved.get("alertId") if saved else None
        alerts = await self._alerts(token)
        if alerts is not None:
            events += alert_events(alerts, last_id)
            last_id = max([last_id or 0, *map(alert_id, alerts)])

        attempted = delivered = 0
        for session in sessions:
            if session.chat_id in logged_out:
                continue
            visible = visible_events(events, session)
            # Опрос шёл токеном одного чата; токен остальных мог быть отозван (сотрудника удалили, сменили роль).
            if visible and (session.token == token or await self._still_signed_in(session)):
                text, keyboard = render(visible, can_write_off=session.allows("stock"))
                attempted += 1
                delivered += await self._send(session, text, keyboard)
        # Состояние продвигается, только когда события дошли хотя бы до одного адресата (или слать было
        # некому): если Telegram не принял ни одного сообщения, следующий опрос пришлёт их снова.
        if delivered or not attempted:
            self._storage.save_state(key, {"at": now.isoformat(), "snapshot": snapshot(*data), "alertId": last_id})
        return events

    def _load_state(self, key: str) -> dict | None:
        """Сохранённое состояние ресторана. Испорченная запись считается отсутствующей: первый опрос заново."""
        try:
            saved = self._storage.load_state(key)
            if saved is None:
                return None
            datetime.fromisoformat(saved["at"])
            if not isinstance(saved["snapshot"], dict):
                raise TypeError("snapshot")
            return saved
        except (ValueError, KeyError, TypeError, AttributeError):
            log.warning("Состояние %s испорчено, начинаю с чистого", key)
            return None

    async def _alerts(self, token: str) -> list[dict] | None:
        """Сигналы ресторана или None, если получить их не удалось: остальные уведомления это не задерживает."""
        try:
            items = (await self._api.get_alerts(token, take=ALERTS_TAKE)).get("items")
        except ApiError as exc:
            log.warning("Сигналы не получены: %s", exc)
            return None
        except (AttributeError, TypeError):
            log.warning("Сигналы пришли в неожиданном виде")
            return None
        return [a for a in items if isinstance(a, dict)] if isinstance(items, list) else None

    async def _still_signed_in(self, session: Session) -> bool:
        """Проверить токен чата перед уведомлением: на 401 — разлогинить и ничего не слать. Другие сбои
        проверки уведомление не задерживают: сервер в любом случае не даст отозванному токену что-то сделать."""
        try:
            await self._api.get_me(session.token)
        except Unauthorized:
            await self._log_out(session)
            return False
        except ApiError as exc:
            log.warning("Токен чата %s проверить не удалось: %s", session.chat_id, exc)
        return True

    async def _log_out(self, session: Session) -> None:
        # Только если в базе всё ещё этот токен: пока шёл опрос, чат мог войти заново.
        if not self._storage.delete_session(session.chat_id, session.token):
            return
        await show_guest_commands(self._bot, session.chat_id)
        await self._send(session, SESSION_EXPIRED + "\nПока вы не войдёте, уведомления не приходят.")

    async def _send(self, session: Session, text: str, keyboard: InlineKeyboardMarkup | None = None) -> bool:
        """True, если Telegram принял сообщение."""
        chat_id = session.chat_id
        for attempt in (1, 2):
            try:
                await self._bot.send_message(chat_id, text, reply_markup=keyboard)
                return True
            except TelegramRetryAfter as exc:
                if attempt == 2:
                    log.warning("Telegram снова просит подождать, уведомление в чат %s не отправлено", chat_id)
                    return False
                log.info("Telegram просит подождать %s с перед отправкой в чат %s", exc.retry_after, chat_id)
                await asyncio.sleep(min(exc.retry_after, MAX_RETRY_AFTER))
            except TelegramForbiddenError:
                # Пользователь заблокировал бота: уведомлять некого, сессия больше не нужна.
                log.info("Чат %s заблокировал бота, сессия удалена", chat_id)
                self._storage.delete_session(chat_id, session.token)
                return False
            except Exception:
                log.exception("Не удалось отправить уведомление в чат %s", chat_id)
                return False
        return False

    async def run(self) -> None:
        while True:
            try:
                await self.poll_once()
            except Exception:
                log.exception("Ошибка в опросе уведомлений")
            await asyncio.sleep(self._poll_seconds)
