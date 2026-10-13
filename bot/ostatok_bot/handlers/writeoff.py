"""Списание партии: кнопка «Списать» → подтверждение → запрос к API."""

from __future__ import annotations

from aiogram import F, Router
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery

from .. import access
from ..api import ApiClient, Conflict, NotFound, ValidationFailed
from ..fmt import fmt_qty
from ..keyboards import confirm
from ..storage import Session
from ..texts import api_error_text, write_off_question
from .util import ack, show

router = Router(name="writeoff")

OTHER_BATCH = "После этого вы выбрали другую партию. Нажмите кнопку под последним вопросом или выберите партию заново."


class WriteOff(StatesGroup):
    confirm = State()


@router.callback_query(F.data.startswith("wo:"))
async def cb_write_off(
    callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session
) -> None:
    if not await access.check(callback, session, "stock", "списывать партии"):
        return
    batch_id = callback.data.removeprefix("wo:")
    batch = next((b for b in await api.get_batches(token) if b["id"] == batch_id), None)
    # Ответ на нажатие — через ack: после долгого запроса к API Telegram его уже не примет.
    if batch is None:
        await ack(callback, "Партия уже списана или израсходована.", show_alert=True)
        return
    await state.clear()
    await state.set_state(WriteOff.confirm)
    await state.update_data(batch=batch)
    await ack(callback)
    # id партии — в самой кнопке: подтверждение под старым сообщением не спишет партию, выбранную позже.
    await callback.message.answer(write_off_question(batch), reply_markup=confirm("Списать", f"woc:{batch['id']}"))


@router.callback_query(WriteOff.confirm, F.data.startswith("woc:"))
async def cb_write_off_confirm(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    batch = (await state.get_data())["batch"]
    if callback.data.removeprefix("woc:") != batch["id"]:
        await callback.answer(OTHER_BATCH, show_alert=True)
        return
    await state.clear()
    what = f"{batch['productName']}, {fmt_qty(batch['remaining'], batch['unit'])}"
    try:
        await api.write_off_batch(token, batch["id"])
    except NotFound:
        await show(callback, "Партия не найдена. Возможно, её уже списали.")
        return
    except Conflict:
        await show(callback, f"В партии уже ничего нет: {batch['productName']}, поставка {batch['supplyNumber']}.")
        return
    except ValidationFailed as exc:
        await show(callback, api_error_text(exc))
        return
    await show(callback, f"Списано: {what}.")
