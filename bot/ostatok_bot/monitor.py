"""Фоновый опрос стоп-листа, партий и сигналов и уведомления вошедшим чатам.

Ресторанов несколько, и каждый токен видит только свой. Поэтому опрос идёт отдельно по каждому
ресторану, где есть вошедшие чаты, токеном одного из этих чатов, а события уходят только им.
Партии видят кладовщик и кухня; официант получает только изменения стоп-листа.

Сроки партий и стоп-лист бот сравнивает сам. Из сигналов сервера (/api/alerts) берутся только остальные
виды: поставка ждёт подтверждения и виды, которых бот ещё не знает, — их текст показывается как есть.

Управляющему бот ещё и напоминает: о партиях «под списание» (срок истёк, сами они со склада не уходят)
и о партиях, которые истекают в ближайшие часы. Одно напоминание на партию; если партию так и не списали —
повтор не чаще раза в REMIND_REPEAT. Другим ролям эти напоминания не уходят.
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
from .fmt import fmt_hours_left, fmt_money, fmt_qty, plural
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

# Напоминания управляющему: кому, о каких партиях «скоро» и как часто повторять про несписанную просрочку.
REMIND_ROLE = "Manager"
REMIND_SOON_HOURS = 6
REMIND_REPEAT = timedelta(hours=4)
# Больше кнопок «Списать» в одном напоминании не ставим: остальное — на сайте.
REMIND_BUTTONS = 8

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
            events.append(Event("batch_expired", f"Под списание: {what} — срок годности истёк", batch))
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


def reminders(batches: list[dict], reminded: dict, now: datetime) -> tuple[list[dict], list[dict], dict]:
    """Партии, о которых напомнить управляющему сейчас: (под списание, истекают скоро, новое состояние напоминаний).
    Состояние — {id партии: {"kind": "due" | "soon", "at": когда напомнили}}; списанные и израсходованные из него уходят."""
    due, soon, state = [], [], {}
    for batch in batches:
        if not batch.get("remaining"):
            continue
        was = reminded.get(batch["id"]) if isinstance(reminded, dict) else None
        try:
            last = datetime.fromisoformat(was["at"]) if isinstance(was, dict) else None
        except (KeyError, TypeError, ValueError):
            last = None
        if batch.get("status") == "Expired":
            if last is not None and was.get("kind") == "due" and now - last < REMIND_REPEAT:
                state[batch["id"]] = was
            else:
                due.append(batch)
                state[batch["id"]] = {"kind": "due", "at": now.isoformat()}
        elif (batch.get("hoursLeft") or 0) <= REMIND_SOON_HOURS:
            if last is not None:
                state[batch["id"]] = was
            else:
                soon.append(batch)
                state[batch["id"]] = {"kind": "soon", "at": now.isoformat()}
    return due, soon, state


def reminder_text(due: list[dict], soon: list[dict]) -> str:
    """Напоминание управляющему: что под списание и на сколько рублей, где списать; что истекает в ближайшие часы."""
    lines = ["Напоминание управляющему."]
    if due:
        values = [b.get("value") for b in due]
        total = f" на {fmt_money(sum(values))}" if all(v is not None for v in values) else ""
        lines.append(f"Под списание — {len(due)} {plural(len(due), 'партия', 'партии', 'партий')}{total}. Срок годности истёк, в заказы они не идут:")
        lines += [f"• {b['productName']}, {fmt_qty(b['remaining'], b['unit'])}, поставка {b['supplyNumber']}" for b in due]
        lines.append("Достаньте их и спишите — кнопкой ниже или на сайте: Склад → партия «Под списание» → «Списать».")
    if soon:
        lines.append(f"Истекают в ближайшие {REMIND_SOON_HOURS} ч:")
        lines += [f"• {b['productName']}, {fmt_qty(b['remaining'], b['unit'])}, {fmt_hours_left(b['hoursLeft'])}" for b in soon]
    return "\n".join(lines)


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
        # Первым опрос идёт токеном управляющего: ему видны закупочные суммы, они нужны в напоминании.
        sessions = sorted(sessions, key=lambda s: s.role != REMIND_ROLE)
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
        # Напоминания — со второго опроса ресторана: первый только запоминает. После простоя они не теряются:
        # повторы отсчитываются по каждой партии.
        reminded = (saved or {}).get("reminded") or {}
        due, soon, reminded = reminders(data[1], reminded, now) if saved is not None else ([], [], reminded)
        reminded_ids = {b["id"] for b in due + soon}

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
            remind = session.role == REMIND_ROLE and bool(due or soon)
            if remind:
                # Партию из напоминания не повторяем отдельной строкой об изменении.
                visible = [e for e in visible if not (e.batch and e.batch["id"] in reminded_ids)]
            # Опрос шёл токеном одного чата; токен остальных мог быть отозван (сотрудника удалили, сменили роль).
            if (visible or remind) and (session.token == token or await self._still_signed_in(session)):
                text, keyboard = render(visible, can_write_off=session.allows("stock"))
                if remind:
                    # Одно сообщение за опрос: изменения, затем напоминание; кнопки «Списать» — и по нему.
                    text = "\n\n".join(t for t in (text, reminder_text(due, soon)) if t)
                    expired = [e.batch for e in visible if e.kind == "batch_expired" and e.batch] + due
                    if expired and session.allows("stock"):
                        keyboard = write_off_keyboard(expired[:REMIND_BUTTONS])
                attempted += 1
                delivered += await self._send(session, text, keyboard)
        # Состояние продвигается, только когда события дошли хотя бы до одного адресата (или слать было
        # некому): если Telegram не принял ни одного сообщения, следующий опрос пришлёт их снова.
        if delivered or not attempted:
            self._storage.save_state(
                key, {"at": now.isoformat(), "snapshot": snapshot(*data), "alertId": last_id, "reminded": reminded}
            )
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
