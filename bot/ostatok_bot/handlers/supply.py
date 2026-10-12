"""/supply: номер → поставщик → продукт → количество → срок → ещё/готово → подтверждение."""

from __future__ import annotations

from zoneinfo import ZoneInfo

from aiogram import F, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient
from ..fmt import InputError, fmt_qty, parse_positive, unit_label
from ..keyboards import cancel_row, confirm, rows
from ..storage import Session, Storage

from .common import SENDING, is_sending
from .pickers import page_from, product_cache, product_picker
from .submit import submit_supply, unfinished_supply_note
from .util import show

router = Router(name="supply")


class SupplyForm(StatesGroup):
    number = State()
    supplier = State()
    product = State()
    quantity = State()
    shelf_life = State()
    more = State()
    confirm = State()
    sending = State()


Step = tuple[str, InlineKeyboardMarkup | None]


def item_line(item: dict, products: dict) -> str:
    product = products[item["productId"]]
    if item.get("shelfLifeHours") is None:
        shelf = f"срок по нормативу ({product['shelfLifeHours']} ч)"
    else:
        shelf = f"срок {item['shelfLifeHours']} ч"
    return f"{product['name']} — {fmt_qty(item['quantity'], product['unit'])}, {shelf}"


# Такие номера выдаёт сама система: заготовкам, проверочным партиям и генератору событий.
SYSTEM_PREFIXES = ("З-", "Т-", "Г-")
MAX_SUPPLIER = 120


def summary_text(data: dict) -> str:
    lines = [f"{n}. {item_line(i, data['products'])}" for n, i in enumerate(data["items"], 1)]
    head = f"Поставка {data['number']}"
    if data.get("supplier"):
        head += f", поставщик {data['supplier']}"
    return f"{head}:\n" + "\n".join(lines) + "\n\nПринять поставку?"


async def product_step(state: FSMContext, data: dict, page: int = 0) -> Step:
    await state.set_state(SupplyForm.product)
    return "Выберите продукт:", product_picker(data["products"], page, "sp:p", "sp:pg")


async def confirm_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(SupplyForm.confirm)
    return summary_text(data), confirm("Принять поставку", "sp:send")


async def more_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(SupplyForm.more)
    added = item_line(data["items"][-1], data["products"])
    text = f"Добавлено: {added}\nПозиций в поставке: {len(data['items'])}"
    keyboard = rows([("Добавить ещё продукт", "sp:add")], [("Готово", "sp:done")], cancel_row())
    return text, keyboard


@router.message(Command("supply"))
async def cmd_supply(
    message: Message, state: FSMContext, api: ApiClient, token: str, session: Session, db: Storage, tz: ZoneInfo
) -> None:
    if not await access.check(message, session, "stock", "принимать поставки"):
        return
    if is_sending(await state.get_state()):
        await message.answer(SENDING)
        return
    note = await unfinished_supply_note(api, db, token, message.chat.id, tz)
    products = await api.list_products(token)
    await state.clear()
    if not products:
        await message.answer(note + "Сначала добавьте продукты: /products")
        return
    await state.set_data({"products": product_cache(products), "items": []})
    await state.set_state(SupplyForm.number)
    await message.answer(
        note + "Новая поставка. Введите номер поставки, например П-0413, "
        "или пришлите фото накладной: бот распознает строки сам.\nОтменить: /cancel"
    )


@router.message(SupplyForm.number)
async def got_number(message: Message, state: FSMContext) -> None:
    number = (message.text or "").strip()
    if not number:
        await message.answer("Номер не может быть пустым. Введите номер поставки.")
        return
    if number.startswith(SYSTEM_PREFIXES):
        await message.answer("Номера на «З-», «Т-» и «Г-» заняты системой. Введите другой номер.")
        return
    data = await state.update_data(number=number)
    # Если номер меняли после ответа 409, продукты уже выбраны — сразу к подтверждению.
    if data["items"]:
        text, keyboard = await confirm_step(state, data)
        await message.answer(text, reply_markup=keyboard)
        return
    await state.set_state(SupplyForm.supplier)
    await message.answer(
        "Поставщик? Его можно не указывать.",
        reply_markup=rows([("Без поставщика", "sp:nosup")], cancel_row()),
    )


@router.message(SupplyForm.supplier)
async def got_supplier(message: Message, state: FSMContext) -> None:
    supplier = (message.text or "").strip()
    if not supplier or len(supplier) > MAX_SUPPLIER:
        await message.answer(f"Название поставщика — от 1 до {MAX_SUPPLIER} символов. Или нажмите «Без поставщика».")
        return
    text, keyboard = await product_step(state, await state.update_data(supplier=supplier))
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(SupplyForm.supplier, F.data == "sp:nosup")
async def cb_no_supplier(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.update_data(supplier=None))
    await show(callback, text, keyboard)


@router.callback_query(SupplyForm.product, F.data.startswith("sp:pg:"))
async def cb_page(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.get_data(), page_from(callback.data))
    await show(callback, text, keyboard)


@router.callback_query(SupplyForm.product, F.data.startswith("sp:p:"))
async def cb_product(callback: CallbackQuery, state: FSMContext) -> None:
    product_id = callback.data.removeprefix("sp:p:")
    data = await state.get_data()
    product = data["products"].get(product_id)
    if product is None:
        await callback.answer("Продукт не найден, выберите другой.", show_alert=True)
        return
    await state.update_data(current=product_id)
    await state.set_state(SupplyForm.quantity)
    await show(callback, f"{product['name']}. Сколько пришло, {unit_label(product['unit'])}?")


@router.message(SupplyForm.quantity)
async def got_quantity(message: Message, state: FSMContext) -> None:
    data = await state.get_data()
    product = data["products"][data["current"]]
    try:
        quantity = parse_positive(message.text)
    except InputError as exc:
        await message.answer(f"{exc} Сколько пришло, {unit_label(product['unit'])}?")
        return
    await state.update_data(quantity=quantity)
    await state.set_state(SupplyForm.shelf_life)
    await message.answer(
        f"Срок хранения в часах? По нормативу — {product['shelfLifeHours']} ч.",
        reply_markup=rows([(f"По нормативу ({product['shelfLifeHours']} ч)", "sp:norm")], cancel_row()),
    )


async def add_item(state: FSMContext, shelf_life_hours: int | None) -> Step:
    data = await state.get_data()
    item = {"productId": data["current"], "quantity": data["quantity"], "shelfLifeHours": shelf_life_hours}
    data = await state.update_data(items=data["items"] + [item])
    return await more_step(state, data)


@router.callback_query(SupplyForm.shelf_life, F.data == "sp:norm")
async def cb_norm(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await add_item(state, None)
    await show(callback, text, keyboard)


@router.message(SupplyForm.shelf_life)
async def got_shelf_life(message: Message, state: FSMContext) -> None:
    try:
        hours = parse_positive(message.text, integer=True)
    except InputError as exc:
        await message.answer(f"{exc} Введите срок хранения в часах или нажмите «По нормативу».")
        return
    text, keyboard = await add_item(state, hours)
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(SupplyForm.more, F.data == "sp:add")
async def cb_add(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.get_data())
    await show(callback, text, keyboard)


@router.callback_query(SupplyForm.more, F.data == "sp:done")
async def cb_done(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await confirm_step(state, await state.get_data())
    await show(callback, text, keyboard)


@router.callback_query(SupplyForm.confirm, F.data == "sp:send")
async def cb_send(
    callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, tz: ZoneInfo, db: Storage
) -> None:
    data = await state.get_data()
    await submit_supply(
        callback, state, api, token, tz, db,
        items=data.get("items") or [], supplier=data.get("supplier"),
        states=(SupplyForm.number, SupplyForm.confirm, SupplyForm.sending),
        send_data="sp:send", restart_hint="Начать заново: /supply",
    )
