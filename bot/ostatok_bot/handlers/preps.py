"""/preps: план заготовок и приготовление со списанием сырья. Доступно кухне (управляющий, шеф, повар)."""

from __future__ import annotations

import uuid
from zoneinfo import ZoneInfo

from aiogram import F, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, Message

from .. import access
from ..api import ApiClient, ValidationFailed
from ..fmt import InputError, fmt_qty, parse_positive, unit_label
from ..keyboards import cancel_row, confirm, rows
from ..storage import Session
from ..texts import api_error_text, prep_result_text, preps_text
from .pickers import page_from
from .util import send, show

router = Router(name="preps")

MAX_PREP = 1000  # предел сервера на одно приготовление


class PrepForm(StatesGroup):
    pick = State()
    quantity = State()
    confirm = State()


@router.message(Command("preps"))
async def cmd_preps(message: Message, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(message, session, "kitchen", "готовить заготовки"):
        return
    preps = await api.get_preps(token)
    await state.clear()
    if not preps:
        await message.answer(preps_text(preps))
        return
    cache = [
        {"id": p["productId"], "name": p["productName"], "unit": p["unit"],
         "suggested": p.get("suggested") or 0, "max": p.get("maxQuantity") or 0}
        for p in preps
    ]
    await state.update_data(preps=cache)
    await state.set_state(PrepForm.pick)
    buttons = [[(f"Приготовить: {p['name']}", f"pp:{i}")] for i, p in enumerate(cache)]
    await send(message, preps_text(preps), rows(*buttons, cancel_row()))


@router.callback_query(PrepForm.pick, F.data.startswith("pp:"))
async def cb_pick(callback: CallbackQuery, state: FSMContext) -> None:
    preps = (await state.get_data())["preps"]
    index = page_from(callback.data)
    if not 0 <= index < len(preps):
        await callback.answer("Такой заготовки нет.", show_alert=True)
        return
    prep = preps[index]
    await state.update_data(current=prep)
    await state.set_state(PrepForm.quantity)
    buttons = []
    if prep["suggested"]:
        buttons.append([(f"По плану: {fmt_qty(prep['suggested'], prep['unit'])}", "pp:plan")])
    await show(
        callback,
        f"{prep['name']}. Сколько приготовить, {unit_label(prep['unit'])}? "
        f"Из сырья хватит на {fmt_qty(prep['max'], prep['unit'])}.",
        rows(*buttons, cancel_row()),
    )


async def ask_confirm(state: FSMContext, quantity: float) -> tuple[str, object]:
    prep = (await state.get_data())["current"]
    await state.update_data(quantity=quantity)
    await state.set_state(PrepForm.confirm)
    text = f"Приготовить {fmt_qty(quantity, prep['unit'])}: {prep['name']}? Сырьё спишется со склада."
    return text, confirm("Приготовить", "pp:ok")


@router.callback_query(PrepForm.quantity, F.data == "pp:plan")
async def cb_plan(callback: CallbackQuery, state: FSMContext) -> None:
    prep = (await state.get_data())["current"]
    text, keyboard = await ask_confirm(state, prep["suggested"])
    await show(callback, text, keyboard)


@router.message(PrepForm.quantity)
async def got_quantity(message: Message, state: FSMContext) -> None:
    prep = (await state.get_data())["current"]
    try:
        quantity = parse_positive(message.text)
        if quantity > MAX_PREP:
            raise InputError(f"За раз — не больше {MAX_PREP}.")
    except InputError as exc:
        await message.answer(f"{exc} Сколько приготовить, {unit_label(prep['unit'])}?")
        return
    text, keyboard = await ask_confirm(state, quantity)
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(PrepForm.confirm, F.data == "pp:ok")
async def cb_make(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, tz: ZoneInfo) -> None:
    data = await state.get_data()
    prep = data["current"]
    await state.clear()
    try:
        # Идентификатор попытки: если ответ потерялся и клиент повторил запрос, сырьё не спишется второй раз.
        result = await api.make_prep(token, prep["id"], data["quantity"], str(uuid.uuid4()))
    except ValidationFailed as exc:
        await show(callback, "Не приготовлено.\n" + api_error_text(exc) + "\nНачать заново: /preps")
        return
    await show(callback, prep_result_text(result, prep["name"], prep["unit"], tz))
