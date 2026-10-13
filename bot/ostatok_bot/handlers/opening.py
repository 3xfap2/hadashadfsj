"""Вскрытие партии: кнопка «Вскрыть» → сколько вскрыть (или вся партия) → запрос к API.
Вскрытая часть становится отдельной партией с коротким сроком, остальное лежит закрытым."""

from __future__ import annotations

from zoneinfo import ZoneInfo

from aiogram import F, Router
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, Message

from .. import access
from ..api import ApiClient, Conflict, NotFound, ValidationFailed
from ..fmt import InputError, fmt_dt, fmt_qty, parse_positive
from ..keyboards import cancel_row, rows
from ..storage import Session
from ..texts import api_error_text, can_open
from .util import ack, show
from .writeoff import OTHER_BATCH

router = Router(name="opening")
ACTION = "вскрывать партии"


class OpenForm(StatesGroup):
    quantity = State()


def question(batch: dict) -> str:
    return (
        f"Вскрыть {batch['productName']} (поставка {batch['supplyNumber']})? "
        f"В партии {fmt_qty(batch['remaining'], batch['unit'])}, после вскрытия хранится "
        f"{batch['openedShelfLifeHours']} ч.\nСколько вскрыть? Введите число или нажмите «Всю партию»."
    )


@router.callback_query(F.data.startswith("op:"))
async def cb_open(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(callback, session, "stock", ACTION):
        return
    batch_id = callback.data.removeprefix("op:")
    batch = next((b for b in await api.get_batches(token) if b["id"] == batch_id), None)
    # Ответ на нажатие — через ack: после долгого запроса к API Telegram его уже не примет.
    if batch is None:
        await ack(callback, "Партия уже израсходована или списана.", show_alert=True)
        return
    if not can_open(batch):
        await ack(callback, "Эту партию вскрыть нельзя: она уже вскрыта или просрочена.", show_alert=True)
        return
    await state.clear()
    await state.set_state(OpenForm.quantity)
    await state.update_data(batch=batch)
    await ack(callback)
    await callback.message.answer(
        question(batch), reply_markup=rows([("Всю партию", f"opall:{batch['id']}")], cancel_row())
    )


@router.message(OpenForm.quantity)
async def got_quantity(message: Message, state: FSMContext, api: ApiClient, token: str, tz: ZoneInfo) -> None:
    batch = (await state.get_data())["batch"]
    try:
        quantity = parse_positive(message.text)
    except InputError as exc:
        await message.answer(f"{exc} Сколько вскрыть?")
        return
    if quantity > batch["remaining"]:
        await message.answer(
            f"В партии только {fmt_qty(batch['remaining'], batch['unit'])}. Введите меньше или нажмите «Всю партию»."
        )
        return
    await state.clear()
    await message.answer(await open_batch(api, token, batch, quantity, tz))


@router.callback_query(OpenForm.quantity, F.data.startswith("opall:"))
async def cb_open_all(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, tz: ZoneInfo) -> None:
    batch = (await state.get_data())["batch"]
    # id партии — в кнопке: «Всю партию» под старым вопросом не вскроет партию, выбранную позже.
    if callback.data.removeprefix("opall:") != batch["id"]:
        await callback.answer(OTHER_BATCH, show_alert=True)
        return
    await state.clear()
    await show(callback, await open_batch(api, token, batch, None, tz))


async def open_batch(api: ApiClient, token: str, batch: dict, quantity: float | None, tz: ZoneInfo) -> str:
    """Вскрыть и вернуть текст ответа. quantity None — весь остаток."""
    try:
        opened = await api.open_batch(token, batch["id"], quantity)
    except NotFound:
        return "Партия не найдена: возможно, её уже израсходовали или списали."
    except (ValidationFailed, Conflict) as exc:
        return "Не вскрыто.\n" + api_error_text(exc)
    amount = fmt_qty(quantity if quantity is not None else batch["remaining"], batch["unit"])
    text = f"Вскрыто: {batch['productName']}, {amount}. Годно до {fmt_dt(opened['expiresAt'], tz)}."
    if quantity is not None and quantity < batch["remaining"]:
        rest = fmt_qty(round(batch["remaining"] - quantity, 3), batch["unit"])
        text += f"\nОстальное ({rest}) лежит закрытым, годно до {fmt_dt(batch['expiresAt'], tz)}."
    return text
