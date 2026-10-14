"""/incoming: поставки, которые прислала внешняя система. На склад они попадают только после приёмки.
Принять можно целиком, частично (по каждой строке — сколько принято, 0 — строку не берут) или отклонить.
Кнопки несут идентификатор поставки и не зависят от состояния диалога: работают и после перезапуска бота."""

from __future__ import annotations

from zoneinfo import ZoneInfo

from aiogram import F, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient, Conflict, NotFound, ValidationFailed
from ..fmt import InputError, fmt_dt, fmt_money, fmt_number, fmt_qty, parse_number
from ..keyboards import cancel_row, rows
from ..storage import Session
from ..texts import api_error_text, supply_result_text
from .util import send, show

router = Router(name="incoming")

# Больше сообщений за раз не шлём: остальные видны в кабинете и после разбора первых.
SHOW_LIMIT = 10
ACTION = "принимать поставки"


class PartialAccept(StatesGroup):
    amounts = State()


def incoming_text(supply: dict, tz: ZoneInfo) -> str:
    head = f"Поставка {supply['number']}"
    if supply.get("supplier"):
        head += f", поставщик {supply['supplier']}"
    if supply.get("supplierInn"):
        head += f", ИНН {supply['supplierInn']}"
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
    return rows(
        [("Принять", f"in:ok:{incoming_id}"), ("Отклонить", f"in:no:{incoming_id}")],
        [("Принять частично", f"in:part:{incoming_id}")],
    )


def partial_question(supply: dict) -> str:
    lines = [f"Поставка {supply['number']}: сколько принято по каждой строке?"]
    for n, item in enumerate(supply.get("items") or [], 1):
        lines.append(f"{n}. {item['productName']} — заказано {fmt_qty(item['quantity'], item['unit'])}")
    lines.append("Напишите числа через пробел по порядку строк, 0 — строку не берёте. Например: "
                 + " ".join("0" if n == 2 else fmt_number(i["quantity"]) for n, i in enumerate(supply["items"], 1)))
    return "\n".join(lines)


def parse_amounts(text: str, items: list[dict]) -> list[float]:
    """Принятое количество по строкам из ответа «3 0 2,5». Ошибка — InputError с понятным текстом."""
    parts = text.replace(";", " ").split()
    if len(parts) != len(items):
        raise InputError(f"Нужно {len(items)} чисел — по одному на строку поставки, через пробел.")
    amounts = []
    for part, item in zip(parts, items):
        value = parse_number(part)
        if value < 0 or value > item["quantity"]:
            raise InputError(f"{item['productName']}: принять можно от 0 до {fmt_qty(item['quantity'], item['unit'])}.")
        amounts.append(value)
    if not any(amounts):
        raise InputError("Ничего не принято. Если поставку не берёте, отклоните её: /incoming")
    return amounts


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


@router.callback_query(F.data.startswith("in:part:"))
async def cb_partial(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(callback, session, "stock", ACTION):
        return
    incoming_id = target(callback.data)
    supply = next((s for s in await api.list_incoming(token) if s["id"] == incoming_id), None)
    if supply is None:
        await show(callback, "Эту поставку уже приняли или отклонили. Список: /incoming")
        return
    await state.clear()
    await state.set_state(PartialAccept.amounts)
    await state.update_data(supply=supply)
    await show(callback, partial_question(supply), rows(cancel_row()))


@router.message(PartialAccept.amounts, F.text, ~F.text.startswith("/"))
async def on_partial_amounts(message: Message, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(message, session, "stock", ACTION):
        return
    supply = (await state.get_data())["supply"]
    items = supply["items"]
    try:
        amounts = parse_amounts(message.text, items)
    except InputError as exc:
        await message.answer(f"{exc}\nПопробуйте ещё раз или нажмите «Отмена».", reply_markup=rows(cancel_row()))
        return
    await state.clear()
    accepted = [{"id": item["id"], "accepted": amount} for item, amount in zip(items, amounts)]
    try:
        await api.accept_incoming(token, supply["id"], accepted)
    except NotFound:
        await message.answer("Эту поставку уже приняли или отклонили. Список: /incoming")
        return
    except (Conflict, ValidationFailed) as exc:
        await message.answer(api_error_text(exc))
        return
    whole = all(amount == item["quantity"] for item, amount in zip(items, amounts))
    lines = [f"Поставка {supply['number']} принята{'' if whole else ' частично'}. На склад встало только принятое:"]
    lines += [
        f"{item['productName']} — принято {fmt_qty(amount, item['unit'])} из {fmt_qty(item['quantity'], item['unit'])}"
        for item, amount in zip(items, amounts)
    ]
    await message.answer("\n".join(lines))


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
