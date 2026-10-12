"""Отправка поставки, общая для ручной (/supply) и по фото.

У каждой попытки свой id поставки. Пока запрос идёт, диалог стоит на шаге «отправляется», поэтому
двойное нажатие не уйдёт вторым запросом. Если ответ потерялся (таймаут), кнопка остаётся: повтор
с тем же id сервер отклонит 409, а бот по списку поставок увидит, что она уже принята, — второй раз
накладная не проводится, а сотрудника не просят ввести «другой номер».
"""

from __future__ import annotations

import logging
import uuid
from zoneinfo import ZoneInfo

from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State
from aiogram.types import CallbackQuery

from ..api import ApiClient, ApiError, ApiUnavailable, Conflict, NotFound, SupplyItemIn, ValidationFailed
from ..keyboards import confirm
from ..storage import Storage
from ..texts import api_error_text, supply_result_text
from .util import ack, result_text, show

log = logging.getLogger(__name__)

RECENT_SUPPLIES = 200  # больше сервер не отдаёт; среди последних есть и системные (заготовки)
UNSURE = (
    "Сервер не ответил, поставка могла пройти. Нажмите «Проверить и принять»: если она уже принята, "
    "бот это увидит и второй раз её не проведёт."
)
NOT_SENT = "Сервер недоступен, поставка не отправлена. Попробуйте ещё раз."
UNKNOWN = "Сервер не ответил, поставка могла пройти. Начните /supply: бот сначала проверит, принята ли она."
NOT_SENT_PLAIN = "Сервер недоступен, поставка не отправлена. Начните заново: /supply"
PENDING_SUPPLY = "supply"


async def find_supply(api: ApiClient, token: str, supply_id: str) -> dict | None:
    for supply in await api.list_supplies(token, take=RECENT_SUPPLIES):
        if supply.get("id") == supply_id:
            return supply
    return None


async def unfinished_supply_note(api: ApiClient, db: Storage, token: str, chat_id: int, tz: ZoneInfo) -> str:
    """Что стало с поставкой, ответ на которую бот не получил (таймаут или перезапуск бота)."""
    pending = db.get_pending(chat_id, PENDING_SUPPLY)
    if not pending:
        return ""
    found = await find_supply(api, token, pending["id"])
    db.delete_pending(chat_id, PENDING_SUPPLY)
    number = pending["data"].get("number") or "без номера"
    if found:
        return f"Прошлая поставка {number} дошла до сервера, повторять её не нужно.\n" + result_text(
            lambda: supply_result_text(found, tz), "Поставка принята."
        ) + "\n\n"
    return f"Прошлая поставка {number}, на которую сервер не ответил, не принята: среди последних поставок её нет.\n\n"


async def submit_supply(
    callback: CallbackQuery,
    state: FSMContext,
    api: ApiClient,
    token: str,
    tz: ZoneInfo,
    db: Storage,
    *,
    items: list[SupplyItemIn],
    supplier: str | None,
    states: tuple[State, State, State],
    send_data: str,
    restart_hint: str,
) -> None:
    """states — шаги диалога: (номер, подтверждение, отправляется)."""
    number_state, confirm_state, sending_state = states
    data = await state.get_data()
    if not items or not data.get("number"):
        # Данных поставки уже нет (диалог сбросили): отправлять нечего.
        await state.clear()
        await callback.answer("Эту поставку уже не отправить. " + restart_hint, show_alert=True)
        return
    chat_id = callback.message.chat.id
    supply_id = data.get("supply_id") or str(uuid.uuid4())
    await state.update_data(supply_id=supply_id)
    # До первого ожидания: второе нажатие уже не пройдёт фильтр «подтверждение».
    await state.set_state(sending_state)
    # id переживает перезапуск бота: следующая поставка сначала проверит, не принята ли уже эта.
    db.save_pending(chat_id, PENDING_SUPPLY, supply_id, {"number": data["number"]})
    settled = False  # сервер ответил определённо: поставка принята или точно не принята
    await ack(callback, "Отправляю поставку…")
    try:
        supply = await api.create_supply(token, data["number"], items, supplier, supply_id)
        settled = True
    except Conflict:
        try:
            ours = await find_supply(api, token, supply_id)
        except ApiError as exc:
            log.warning("Не удалось проверить, принята ли поставка %s: %s", supply_id, exc)
            await state.clear()
            await show(callback, "Поставка с таким номером или идентификатором уже есть. "
                                 "Проверьте /journal: возможно, она уже принята.")
            return
        settled = True
        if ours:
            await state.clear()
            text = result_text(lambda: supply_result_text(ours, tz), "Поставка принята.")
            await show(callback, "Поставка уже была принята при прошлой попытке.\n\n" + text)
            return
        if await state.get_state() == sending_state.state:
            await state.set_state(number_state)
        await show(callback, f"Поставка с номером {data['number']} уже есть. Введите другой номер.")
        return
    except (ValidationFailed, NotFound) as exc:
        settled = True
        await state.clear()
        fallback = "Какой-то продукт не найден, возможно его удалили." if isinstance(exc, NotFound) else None
        await show(callback, "Поставка не принята.\n" + api_error_text(exc, fallback) + "\n" + restart_hint)
        return
    except ApiUnavailable as exc:
        log.warning("Поставка %s не отправлена: %s %r", supply_id, exc, exc.body)
        if exc.from_app and not exc.unsure:
            settled = True
            await state.clear()
            text = api_error_text(exc, f"Ошибка сервера (код {exc.status}).")
            await show(callback, "Поставка не принята.\n" + text + "\n" + restart_hint)
            return
        settled = not exc.unsure
        if await state.get_state() != sending_state.state:
            # Пока шла отправка, диалог сбросили (другая команда): кнопки с прежними данными уже нет.
            await show(callback, UNKNOWN if exc.unsure else NOT_SENT_PLAIN)
            return
        await state.set_state(confirm_state)
        if exc.unsure:
            await show(callback, UNSURE, confirm("Проверить и принять", send_data))
        else:
            await show(callback, NOT_SENT, confirm("Принять поставку", send_data))
        return
    except ApiError:
        settled = True  # 401, 403 и другие отказы приложения: поставка не принята, ответит общий обработчик
        raise
    finally:
        if settled:
            db.delete_pending(chat_id, PENDING_SUPPLY)
        # Ошибка, которую ловят общие обработчики (401, 403…): не оставлять диалог в «отправляется».
        if await state.get_state() == sending_state.state:
            await state.set_state(confirm_state)
    await state.clear()
    await show(callback, result_text(lambda: supply_result_text(supply, tz), "Поставка отправлена, сервер ответил успехом."))
