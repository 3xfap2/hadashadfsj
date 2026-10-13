"""/products: список, добавление, изменение (с «Оставить»), удаление."""

from __future__ import annotations

from aiogram import F, Router
from aiogram.filters import Command, StateFilter
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient, Conflict, NotFound, ValidationFailed
from ..fmt import UNIT_LABELS, InputError, fmt_money, parse_money, parse_positive
from ..keyboards import cancel_row, confirm, rows
from ..storage import Session
from ..texts import api_error_text, product_line, products_text
from .pickers import page_from, product_cache, product_picker
from .util import ack, send, show

router = Router(name="products")

FIELDS = ["name", "unit", "shelfLifeHours", "cost"]
KEEP = "pr:keep"


class ProductForm(StatesGroup):
    pick = State()
    name = State()
    unit = State()
    shelfLifeHours = State()
    cost = State()
    confirm = State()
    delete_confirm = State()


Step = tuple[str, InlineKeyboardMarkup | None]

PICK_PROMPTS = {"edit": "Какой продукт изменить?", "delete": "Какой продукт удалить?"}


def list_keyboard(has_products: bool, can_edit: bool) -> InlineKeyboardMarkup | None:
    if not can_edit:
        return None
    buttons = [("Добавить", "pr:add")]
    if has_products:
        buttons += [("Изменить", "pr:edit"), ("Удалить", "pr:del")]
    return rows(buttons)


def current_value(field: str, product: dict) -> str:
    value = product[field]
    if field == "unit":
        return UNIT_LABELS.get(value, value)
    if field == "shelfLifeHours":
        return f"{value} ч"
    if field == "cost":
        return fmt_money(value)
    return f"«{value}»"


QUESTIONS = {
    "name": "Название продукта?",
    "unit": "Единица измерения?",
    "shelfLifeHours": "Срок хранения в часах?",
    "cost": "Закупочная цена за единицу, ₽?",
}


async def field_step(state: FSMContext, data: dict, field: str) -> Step:
    await state.set_state(getattr(ProductForm, field))
    question = QUESTIONS[field]
    keyboard_rows = []
    if field == "unit":
        keyboard_rows.append([(label, f"pr:u:{code}") for code, label in UNIT_LABELS.items()])
    if data["mode"] == "edit":
        question += f"\nСейчас: {current_value(field, data['original'])}."
        keyboard_rows.append([("Оставить", KEEP)])
    keyboard_rows.append(cancel_row())
    return question, rows(*keyboard_rows)


async def confirm_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(ProductForm.confirm)
    draft = data["draft"]
    if data["mode"] == "add":
        text = f"Новый продукт:\n{product_line(draft)}\n\nСохранить?"
    else:
        text = f"Было: {product_line(data['original'])}\nСтанет: {product_line(draft)}\n\nСохранить изменения?"
    return text, confirm("Сохранить", "pr:save")


def is_prep(data: dict) -> bool:
    return bool(data.get("original", {}).get("prepRecipe"))


async def next_step(state: FSMContext, data: dict, done_field: str) -> Step:
    index = FIELDS.index(done_field)
    if data.get("back_to_confirm") or index == len(FIELDS) - 1:
        return await confirm_step(state, data)
    following = FIELDS[index + 1]
    # Цену заготовки сервер считает по её составу: спрашивать её незачем.
    if following == "cost" and is_prep(data):
        return await confirm_step(state, data)
    return await field_step(state, data, following)


async def set_field(state: FSMContext, field: str, value) -> Step:
    data = await state.get_data()
    data = await state.update_data(draft={**data["draft"], field: value})
    return await next_step(state, data, field)


# Список


@router.message(Command("products"))
async def cmd_products(message: Message, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    await state.clear()
    products = await api.list_products(token)
    await send(message, products_text(products), list_keyboard(bool(products), session.allows("catalog")))


@router.callback_query(F.data == "pr:add")
async def cb_add(callback: CallbackQuery, state: FSMContext, session: Session) -> None:
    if not await access.check(callback, session, "catalog", "менять справочник продуктов"):
        return
    await state.clear()
    data = await state.update_data(mode="add", draft={})
    text, keyboard = await field_step(state, data, "name")
    await callback.answer()
    await callback.message.answer("Новый продукт. Отменить: /cancel\n\n" + text, reply_markup=keyboard)


@router.callback_query(F.data.in_({"pr:edit", "pr:del"}))
async def cb_pick_start(
    callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session
) -> None:
    if not await access.check(callback, session, "catalog", "менять справочник продуктов"):
        return
    products = product_cache(await api.list_products(token))
    if not products:
        await ack(callback, "Продуктов пока нет.", show_alert=True)
        return
    mode = "edit" if callback.data == "pr:edit" else "delete"
    await state.clear()
    await state.update_data(mode=mode, products=products)
    await state.set_state(ProductForm.pick)
    await ack(callback)
    await callback.message.answer(PICK_PROMPTS[mode], reply_markup=product_picker(products, 0, "pr:e", "pr:pg"))


@router.callback_query(ProductForm.pick, F.data.startswith("pr:pg:"))
async def cb_page(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    keyboard = product_picker(data["products"], page_from(callback.data), "pr:e", "pr:pg")
    await show(callback, PICK_PROMPTS[data["mode"]], keyboard)


@router.callback_query(ProductForm.pick, F.data.startswith("pr:e:"))
async def cb_pick(callback: CallbackQuery, state: FSMContext) -> None:
    product_id = callback.data.removeprefix("pr:e:")
    data = await state.get_data()
    product = data["products"].get(product_id)
    if product is None:
        await callback.answer("Продукт не найден.", show_alert=True)
        return
    original = {**product}
    data = await state.update_data(product_id=product_id, original=original, draft=dict(original))
    if data["mode"] == "delete":
        await state.set_state(ProductForm.delete_confirm)
        await show(callback, f"Удалить продукт «{product['name']}»?", confirm("Удалить", "pr:delok"))
        return
    text, keyboard = await field_step(state, data, "name")
    await show(callback, text, keyboard)


# Поля


@router.callback_query(
    F.data == KEEP,
    StateFilter(ProductForm.name, ProductForm.unit, ProductForm.shelfLifeHours, ProductForm.cost),
)
async def cb_keep(callback: CallbackQuery, state: FSMContext) -> None:
    field = (await state.get_state()).split(":", 1)[1]
    text, keyboard = await next_step(state, await state.get_data(), field)
    await show(callback, text, keyboard)


# Команда на этом шаге — не название: она уходит своему обработчику.
@router.message(ProductForm.name, ~F.text.startswith("/"))
async def got_name(message: Message, state: FSMContext) -> None:
    name = (message.text or "").strip()
    if not name:
        await message.answer("Название не может быть пустым. Введите название продукта.")
        return
    text, keyboard = await set_field(state, "name", name)
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(ProductForm.unit, F.data.startswith("pr:u:"))
async def cb_unit(callback: CallbackQuery, state: FSMContext) -> None:
    unit = callback.data.removeprefix("pr:u:")
    if unit not in UNIT_LABELS:
        await callback.answer("Неизвестная единица.", show_alert=True)
        return
    text, keyboard = await set_field(state, "unit", unit)
    await show(callback, text, keyboard)


@router.message(ProductForm.unit)
async def got_unit_text(message: Message) -> None:
    await message.answer("Выберите единицу кнопкой: кг, л или шт.")


@router.message(ProductForm.shelfLifeHours)
async def got_hours(message: Message, state: FSMContext) -> None:
    try:
        hours = parse_positive(message.text, integer=True)
    except InputError as exc:
        await message.answer(f"{exc} Срок хранения в часах?")
        return
    text, keyboard = await set_field(state, "shelfLifeHours", hours)
    await message.answer(text, reply_markup=keyboard)


@router.message(ProductForm.cost)
async def got_cost(message: Message, state: FSMContext) -> None:
    try:
        cost = parse_money(message.text)
    except InputError as exc:
        await message.answer(f"{exc} Закупочная цена за единицу, ₽?")
        return
    text, keyboard = await set_field(state, "cost", cost)
    await message.answer(text, reply_markup=keyboard)


# Сохранение и удаление


@router.callback_query(ProductForm.confirm, F.data == "pr:save")
async def cb_save(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    d = data["draft"]
    try:
        if data["mode"] == "add":
            saved = await api.create_product(token, d["name"], d["unit"], d["shelfLifeHours"], d["cost"])
            done = "Продукт добавлен"
        else:
            saved = await api.update_product(
                token, data["product_id"], d["name"], d["unit"], d["shelfLifeHours"], d["cost"],
                data["original"].get("prepRecipe") or None,
                data["original"].get("openedShelfLifeHours"),
            )
            done = "Сохранено"
    except Conflict as exc:
        data = await state.update_data(back_to_confirm=True)
        if exc.detail and "назван" not in exc.detail:
            # Единицу нельзя сменить у продукта с поставками или техкартами: вернуть к выбору единицы.
            text, keyboard = await field_step(state, data, "unit")
            await show(callback, f"{exc.detail}\n\n{text}", keyboard)
            return
        await state.set_state(ProductForm.name)
        await show(callback, f"Продукт «{d['name']}» уже есть. Введите другое название.")
        return
    except NotFound:
        await state.clear()
        await show(callback, "Продукт не найден, возможно его уже удалили. Список: /products")
        return
    except ValidationFailed as exc:
        await state.clear()
        await show(callback, "Не сохранено.\n" + api_error_text(exc) + "\nПопробуйте снова: /products")
        return
    await state.clear()
    await show(callback, f"{done}: {product_line(saved or d)}")


@router.callback_query(ProductForm.delete_confirm, F.data == "pr:delok")
async def cb_delete(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    name = data["original"]["name"]
    await state.clear()
    try:
        await api.delete_product(token, data["product_id"])
    except NotFound:
        await show(callback, f"Продукт «{name}» не найден, возможно его уже удалили.")
        return
    except Conflict:
        await show(callback, f"Продукт «{name}» используется в поставках или техкартах, удалить нельзя.")
        return
    await show(callback, f"Продукт «{name}» удалён.")
