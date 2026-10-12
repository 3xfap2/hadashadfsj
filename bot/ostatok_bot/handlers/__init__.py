from aiogram import Router
from aiogram.fsm.context import FSMContext
from aiogram.types import CallbackQuery, Message

from . import (
    auth, common, dishes, incoming, inventory, opening, order, photo_supply, preps, products, supply, view, writeoff,
)

fallback = Router(name="fallback")


@fallback.message()
async def unknown_message(message: Message, state: FSMContext) -> None:
    if await state.get_state():
        await message.answer("Выберите вариант кнопкой выше или прервите действие: /cancel")
    else:
        await message.answer("Не понял. Список команд: /start")


@fallback.callback_query()
async def stale_button(callback: CallbackQuery) -> None:
    await callback.answer("Эта кнопка уже не работает. Начните действие заново.", show_alert=True)


def build_router() -> Router:
    root = Router(name="root")
    # common первым: /cancel должен срабатывать на любом шаге любого диалога.
    root.include_routers(
        common.router,
        auth.router,
        view.router,
        writeoff.router,
        opening.router,
        # фото раньше поставки: шаг номера в /supply принимает любое сообщение
        photo_supply.router,
        supply.router,
        incoming.router,
        inventory.router,
        order.router,
        preps.router,
        products.router,
        dishes.router,
        fallback,
    )
    return root
