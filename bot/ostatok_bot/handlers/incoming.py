"""/incoming: поставки, которые прислала внешняя система. На склад они попадают только после приёмки.
Кнопки несут идентификатор поставки и не зависят от состояния диалога: работают и после перезапуска бота."""

from __future__ import annotations

from zoneinfo import ZoneInfo

from aiogram import F, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient, Conflict, NotFound
from ..fmt import fmt_dt, fmt_money, fmt_qty
from ..keyboards import rows
from ..storage import Session
from ..texts import api_error_text, supply_result_text
from .util import send, show

router = Router(name="incoming")

# Больше сообщений за раз не шлём: остальные видны в кабинете и после разбора первых.
SHOW_LIMIT = 10
ACTION = "принимать поставки"


def incoming_text(supply: dict, tz: ZoneInfo) -> str:
    head = f"Поставка {supply['number']}"
    if supply.get("supplier"):
        head += f", поставщик {supply['supplier']}"
    lines = [f"{head}\nПришла {fmt_dt(supply['receivedAt'], tz)}, ждёт подтверждения."]
    if supply.get("createdAt"):
        # Срок годности сервер считает от даты поставки, а не от приёмки.
        lines.append(f"Дата поставки: {fmt_dt(supply['createdAt'], tz)}, сроки считаются от неё.")
    for n, item in enumerate(supply.get("items") or [], 1):
        lines.append(
            f"{n}. {item['productName']} — {fmt_qty(item['quantity'], item['unit'])}, срок {item['shelfLifeHours']} ч"
        )
    lines.append(f"Сумма: {fmt_money(supply.get('total') or 0)}")
    return "\n".join(lines)


def decide_keyboard(incoming_id: str) -> InlineKeyboardMarkup:
    return rows([("Принять", f"in:ok:{incoming_id}"), ("Отклонить", f"in:no:{incoming_id}")])


def target(data: str) -> str:
    return data.split(":", 2)[2]


@router.message(Command("incoming"))
async def cmd_incoming(message: Message, state: FSMContext, api: ApiClient, token: str, session: Session, tz: ZoneInfo) -> None:
    if not await access.check(message, session, "stock", ACTION):
        return
    await state.clear()
    waiting = await api.list_incoming(token)
    if not waiting:
        await message.answer("Поставок, которые ждут подтверждения, нет.")
        return
    count = len(waiting)
    head = f"Ждут подтверждения: {count}." if count > 1 else "Ждёт подтверждения одна поставка."
    if count > SHOW_LIMIT:
        head += f" Показаны первые {SHOW_LIMIT}, остальные — после разбора этих или в кабинете."
    await message.answer(head)
    for supply in waiting[:SHOW_LIMIT]:
        await send(message, incoming_text(supply, tz), decide_keyboard(supply["id"]))


@router.callback_query(F.data.startswith("in:ok:"))
async def cb_accept(callback: CallbackQuery, api: ApiClient, token: str, session: Session, tz: ZoneInfo) -> None:
    if not await access.check(callback, session, "stock", ACTION):
        return
    incoming_id = target(callback.data)
    try:
        supply = await api.accept_incoming(token, incoming_id)
    except NotFound:
        await show(callback, "Эту поставку уже приняли или отклонили. Список: /incoming")
        return
    except Conflict as exc:
        # Поставка с таким номером уже на складе: принять вторую нельзя, остаётся отклонить.
        await show(callback, api_error_text(exc), rows([("Отклонить", f"in:no:{incoming_id}")]))
        return
    await show(callback, supply_result_text(supply, tz))


@router.callback_query(F.data.startswith("in:no:"))
async def cb_reject_ask(callback: CallbackQuery, session: Session) -> None:
    if not await access.check(callback, session, "stock", ACTION):
        return
    incoming_id = target(callback.data)
    text = (callback.message.text or "Поставка") if callback.message else "Поставка"
    await show(
        callback,
        f"{text}\n\nОтклонить поставку? На склад она не попадёт, вернуть её из бота нельзя.",
        rows([("Да, отклонить", f"in:rej:{incoming_id}")], [("Не отклонять", f"in:keep:{incoming_id}")]),
    )


@router.callback_query(F.data.startswith("in:keep:"))
async def cb_reject_keep(callback: CallbackQuery, api: ApiClient, token: str, tz: ZoneInfo) -> None:
    incoming_id = target(callback.data)
    supply = next((s for s in await api.list_incoming(token) if s["id"] == incoming_id), None)
    if supply is None:
        await show(callback, "Эту поставку уже приняли или отклонили. Список: /incoming")
        return
    await show(callback, incoming_text(supply, tz), decide_keyboard(incoming_id))


@router.callback_query(F.data.startswith("in:rej:"))
async def cb_reject(callback: CallbackQuery, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(callback, session, "stock", ACTION):
        return
    try:
        await api.reject_incoming(token, target(callback.data))
    except NotFound:
        await show(callback, "Эту поставку уже приняли или отклонили. Список: /incoming")
        return
    await show(callback, "Поставка отклонена, на склад она не попала.")
