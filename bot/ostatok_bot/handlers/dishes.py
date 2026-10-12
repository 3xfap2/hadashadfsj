"""/dishes: список блюд, карточка с составом, добавление, изменение, удаление.

Префиксы кнопок: ds — список и карточка, de — меню изменения, dr — состав.
"""

from __future__ import annotations

from aiogram import F, Router
from aiogram.filters import Command, StateFilter
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient, Conflict, NotFound, ValidationFailed
from ..fmt import InputError, fmt_money, fmt_qty, parse_money, parse_positive, unit_label
from ..keyboards import cancel_row, confirm, paged, rows
from ..storage import Session
from ..texts import api_error_text, dish_card_text
from .pickers import page_from, product_cache, product_picker
from .util import show

router = Router(name="dishes")

EMPTY_RECIPE = "В составе должен быть хотя бы один продукт."


class DishForm(StatesGroup):
    name = State()
    price = State()
    menu = State()
    recipe_product = State()
    recipe_qty = State()
    recipe_more = State()
    recipe_edit = State()
    recipe_item = State()
    confirm = State()
    delete_confirm = State()
    pause_confirm = State()


Step = tuple[str, InlineKeyboardMarkup | None]


def draft_card(data: dict) -> str:
    d = data["draft"]
    return dish_card_text(
        d.get("name", ""), d.get("price", 0), d.get("recipe", []), data["products"], d.get("category")
    )


# Список и карточка


async def list_step(api: ApiClient, token: str, session: Session, page: int = 0) -> Step:
    dishes = sorted(await api.list_dishes(token), key=lambda d: d["name"].lower())
    buttons = [
        (f"{d['name']} — {'снято с продажи' if d.get('manuallyStopped') else fmt_money(d['price'])}", f"ds:v:{d['id']}")
        for d in dishes
    ]
    text = f"Блюд: {len(dishes)}. Нажмите на блюдо, чтобы увидеть цену и состав." if dishes else "Блюд пока нет."
    extra = [[("Добавить", "ds:add")]] if session.allows("catalog") else None
    return text, paged(buttons, page, "ds:pg", extra)


@router.message(Command("dishes"))
async def cmd_dishes(message: Message, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    await state.clear()
    text, keyboard = await list_step(api, token, session)
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(F.data.startswith("ds:pg:"))
async def cb_list_page(callback: CallbackQuery, api: ApiClient, token: str, session: Session) -> None:
    text, keyboard = await list_step(api, token, session, page_from(callback.data))
    await show(callback, text, keyboard)


async def load_dish(api: ApiClient, token: str, dish_id: str) -> tuple[dict | None, dict]:
    dish = next((d for d in await api.list_dishes(token) if d["id"] == dish_id), None)
    products = product_cache(await api.list_products(token))
    return dish, products


@router.callback_query(F.data.startswith("ds:v:"))
async def cb_view(callback: CallbackQuery, api: ApiClient, token: str, session: Session) -> None:
    dish_id = callback.data.removeprefix("ds:v:")
    dish, products = await load_dish(api, token, dish_id)
    if dish is None:
        await callback.answer("Блюдо не найдено, возможно его удалили.", show_alert=True)
        return
    text = dish_card_text(
        dish["name"], dish["price"], dish["recipe"], products, dish.get("category"), dish.get("manuallyStopped", False)
    )
    buttons = []
    if session.allows("catalog"):
        buttons.append([("Изменить", f"ds:e:{dish_id}"), ("Удалить", f"ds:d:{dish_id}")])
    if session.allows("kitchen"):
        if dish.get("manuallyStopped"):
            buttons.append([("Вернуть в продажу", f"ds:on:{dish_id}")])
        else:
            buttons.append([("Снять с продажи", f"ds:off:{dish_id}")])
    buttons.append([("К списку", "ds:pg:0")])
    await show(callback, text, rows(*buttons))


# Ручной стоп: кухня снимает блюдо с продажи и возвращает его


@router.callback_query(F.data.startswith("ds:off:") | F.data.startswith("ds:on:"))
async def cb_pause_ask(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(callback, session, "kitchen", "снимать блюда с продажи"):
        return
    stop = callback.data.startswith("ds:off:")
    dish_id = callback.data.split(":", 2)[2]
    dish = next((d for d in await api.list_dishes(token) if d["id"] == dish_id), None)
    if dish is None:
        await callback.answer("Блюдо не найдено, возможно его удалили.", show_alert=True)
        return
    await state.clear()
    await state.update_data(dish_id=dish_id, dish_name=dish["name"], stop=stop)
    await state.set_state(DishForm.pause_confirm)
    if stop:
        question, button = f"Снять «{dish['name']}» с продажи? Официанты не смогут его заказать.", "Снять с продажи"
    else:
        question, button = f"Вернуть «{dish['name']}» в продажу?", "Вернуть в продажу"
    await show(callback, question, confirm(button, "ds:pauseok"))


@router.callback_query(DishForm.pause_confirm, F.data == "ds:pauseok")
async def cb_pause(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    await state.clear()
    name = data["dish_name"]
    try:
        if data["stop"]:
            await api.stop_dish(token, data["dish_id"])
        else:
            await api.resume_dish(token, data["dish_id"])
    except NotFound:
        await show(callback, f"Блюдо «{name}» не найдено, возможно его удалили.")
        return
    if data["stop"]:
        await show(callback, f"«{name}» снято с продажи. Вернуть: /dishes → блюдо → «Вернуть в продажу».")
    else:
        await show(callback, f"«{name}» снова в продаже, если на него хватает продуктов.")


# Добавление


@router.callback_query(F.data == "ds:add")
async def cb_add(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(callback, session, "catalog", "менять меню"):
        return
    products = product_cache(await api.list_products(token))
    await state.clear()
    if not products:
        await callback.answer("Сначала добавьте продукты: /products", show_alert=True)
        return
    await state.update_data(mode="add", products=products, draft={"recipe": []})
    await state.set_state(DishForm.name)
    await callback.answer()
    await callback.message.answer("Новое блюдо. Отменить: /cancel\n\nНазвание блюда?", reply_markup=rows(cancel_row()))


# Изменение: меню


async def menu_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(DishForm.menu)
    keyboard = rows(
        [("Название", "de:name"), ("Цена", "de:price"), ("Состав", "de:recipe")],
        [("Сохранить", "de:save")],
        cancel_row(),
    )
    return draft_card(data) + "\n\nЧто изменить?", keyboard


@router.callback_query(F.data.startswith("ds:e:"))
async def cb_edit(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(callback, session, "catalog", "менять меню"):
        return
    dish_id = callback.data.removeprefix("ds:e:")
    dish, products = await load_dish(api, token, dish_id)
    if dish is None:
        await callback.answer("Блюдо не найдено, возможно его удалили.", show_alert=True)
        return
    await state.clear()
    draft = {
        "name": dish["name"],
        "price": dish["price"],
        "recipe": [dict(r) for r in dish["recipe"]],
        # Поля, которых бот не меняет, передаются как были: без category API уберёт блюдо из раздела меню.
        "photoUrl": dish.get("photoUrl"),
        "category": dish.get("category"),
    }
    data = await state.update_data(mode="edit", dish_id=dish_id, products=products, draft=draft)
    text, keyboard = await menu_step(state, data)
    await show(callback, text, keyboard)


@router.callback_query(DishForm.menu, F.data == "de:name")
async def cb_edit_name(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    await state.set_state(DishForm.name)
    await show(
        callback,
        f"Новое название? Сейчас: «{data['draft']['name']}».",
        rows([("Назад", "de:menu")], cancel_row()),
    )


@router.callback_query(DishForm.menu, F.data == "de:price")
async def cb_edit_price(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    await state.set_state(DishForm.price)
    await show(
        callback,
        f"Новая цена, ₽? Сейчас: {fmt_money(data['draft']['price'])}.",
        rows([("Назад", "de:menu")], cancel_row()),
    )


@router.callback_query(StateFilter(DishForm.name, DishForm.price, DishForm.recipe_edit), F.data.in_({"de:menu", "dr:back"}))
async def cb_back_to_menu(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    if callback.data == "dr:back" and not data["draft"]["recipe"]:
        await callback.answer(EMPTY_RECIPE, show_alert=True)
        return
    text, keyboard = await menu_step(state, data)
    await show(callback, text, keyboard)


@router.callback_query(DishForm.menu, F.data == "de:save")
async def cb_edit_save(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    if not data["draft"]["recipe"]:
        await callback.answer(EMPTY_RECIPE, show_alert=True)
        return
    text, keyboard = await confirm_step(state, data)
    await show(callback, text, keyboard)


# Название и цена


@router.message(DishForm.name)
async def got_name(message: Message, state: FSMContext) -> None:
    name = (message.text or "").strip()
    if not name:
        await message.answer("Название не может быть пустым. Введите название блюда.")
        return
    data = await state.get_data()
    data = await state.update_data(draft={**data["draft"], "name": name})
    if data["mode"] == "edit":
        text, keyboard = await menu_step(state, data)
    elif data.get("back_to_confirm"):
        text, keyboard = await confirm_step(state, data)
    else:
        await state.set_state(DishForm.price)
        text, keyboard = "Цена блюда, ₽?", rows(cancel_row())
    await message.answer(text, reply_markup=keyboard)


@router.message(DishForm.price)
async def got_price(message: Message, state: FSMContext) -> None:
    try:
        price = parse_money(message.text)
    except InputError as exc:
        await message.answer(f"{exc} Цена блюда, ₽?")
        return
    data = await state.get_data()
    data = await state.update_data(draft={**data["draft"], "price": price})
    if data["mode"] == "edit":
        text, keyboard = await menu_step(state, data)
    else:
        text, keyboard = await product_step(state, data)
        text = "Теперь состав. " + text
    await message.answer(text, reply_markup=keyboard)


# Состав


async def product_step(state: FSMContext, data: dict, page: int = 0) -> Step:
    await state.set_state(DishForm.recipe_product)
    extra = [[("Назад", "dr:list")]] if data["mode"] == "edit" else None
    return "Выберите продукт:", product_picker(data["products"], page, "dr:p", "dr:pg", extra)


def recipe_item_label(item: dict, products: dict) -> str:
    product = products.get(item["productId"])
    if product is None:
        return f"Неизвестный продукт: {item['quantity']}"
    return f"{product['name']}: {fmt_qty(item['quantity'], product['unit'])}"


async def recipe_editor_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(DishForm.recipe_edit)
    recipe = data["draft"]["recipe"]
    item_rows = [[(recipe_item_label(r, data["products"]), f"dr:i:{r['productId']}")] for r in recipe]
    keyboard = rows(*item_rows, [("Добавить продукт", "dr:add")], [("Готово", "dr:back")])
    text = "Состав. Нажмите на продукт, чтобы изменить количество или убрать его."
    if not recipe:
        text = "Состав пуст. " + EMPTY_RECIPE
    return text, keyboard


async def recipe_more_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(DishForm.recipe_more)
    lines = "\n".join(recipe_item_label(r, data["products"]) for r in data["draft"]["recipe"])
    keyboard = rows([("Добавить ещё продукт", "dr:add")], [("Готово", "dr:done")], cancel_row())
    return f"Состав:\n{lines}", keyboard


@router.callback_query(DishForm.menu, F.data == "de:recipe")
@router.callback_query(StateFilter(DishForm.recipe_item, DishForm.recipe_product), F.data == "dr:list")
async def cb_recipe_editor(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await recipe_editor_step(state, await state.get_data())
    await show(callback, text, keyboard)


@router.callback_query(StateFilter(DishForm.recipe_edit, DishForm.recipe_more), F.data == "dr:add")
async def cb_recipe_add(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.get_data())
    await show(callback, text, keyboard)


@router.callback_query(DishForm.recipe_product, F.data.startswith("dr:pg:"))
async def cb_recipe_page(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.get_data(), page_from(callback.data))
    await show(callback, text, keyboard)


def quantity_question(data: dict, product_id: str) -> str:
    product = data["products"][product_id]
    question = f"Сколько «{product['name']}» на порцию, {unit_label(product['unit'])}?"
    existing = next((r for r in data["draft"]["recipe"] if r["productId"] == product_id), None)
    if existing:
        question += f" Сейчас: {fmt_qty(existing['quantity'], product['unit'])}."
    return question


@router.callback_query(DishForm.recipe_product, F.data.startswith("dr:p:"))
async def cb_recipe_product(callback: CallbackQuery, state: FSMContext) -> None:
    product_id = callback.data.removeprefix("dr:p:")
    data = await state.get_data()
    if product_id not in data["products"]:
        await callback.answer("Продукт не найден.", show_alert=True)
        return
    await state.update_data(current_product=product_id)
    await state.set_state(DishForm.recipe_qty)
    await show(callback, quantity_question(data, product_id))


@router.callback_query(DishForm.recipe_edit, F.data.startswith("dr:i:"))
async def cb_recipe_item(callback: CallbackQuery, state: FSMContext) -> None:
    product_id = callback.data.removeprefix("dr:i:")
    data = await state.update_data(current_product=product_id)
    item = next((r for r in data["draft"]["recipe"] if r["productId"] == product_id), None)
    if item is None:
        await callback.answer("Этого продукта уже нет в составе.", show_alert=True)
        return
    await state.set_state(DishForm.recipe_item)
    keyboard = rows([("Изменить количество", "dr:q"), ("Убрать", "dr:rm")], [("Назад", "dr:list")])
    await show(callback, recipe_item_label(item, data["products"]), keyboard)


@router.callback_query(DishForm.recipe_item, F.data == "dr:q")
async def cb_recipe_change_qty(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    if data["current_product"] not in data["products"]:
        await callback.answer("Продукт не найден, его можно только убрать.", show_alert=True)
        return
    await state.set_state(DishForm.recipe_qty)
    await show(callback, quantity_question(data, data["current_product"]))


@router.callback_query(DishForm.recipe_item, F.data == "dr:rm")
async def cb_recipe_remove(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    recipe = [r for r in data["draft"]["recipe"] if r["productId"] != data["current_product"]]
    data = await state.update_data(draft={**data["draft"], "recipe": recipe})
    text, keyboard = await recipe_editor_step(state, data)
    await show(callback, text, keyboard)


@router.message(DishForm.recipe_qty)
async def got_recipe_qty(message: Message, state: FSMContext) -> None:
    data = await state.get_data()
    product_id = data["current_product"]
    try:
        quantity = parse_positive(message.text)
    except InputError as exc:
        await message.answer(f"{exc} {quantity_question(data, product_id)}")
        return
    recipe = [dict(r) for r in data["draft"]["recipe"]]
    existing = next((r for r in recipe if r["productId"] == product_id), None)
    if existing:
        existing["quantity"] = quantity
    else:
        recipe.append({"productId": product_id, "quantity": quantity})
    data = await state.update_data(draft={**data["draft"], "recipe": recipe})
    step = recipe_editor_step if data["mode"] == "edit" else recipe_more_step
    text, keyboard = await step(state, data)
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(DishForm.recipe_more, F.data == "dr:done")
async def cb_recipe_done(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await confirm_step(state, await state.get_data())
    await show(callback, text, keyboard)


# Подтверждение и отправка


async def confirm_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(DishForm.confirm)
    question = "Сохранить блюдо?" if data["mode"] == "add" else "Сохранить изменения?"
    return f"{draft_card(data)}\n\n{question}", confirm("Сохранить", "ds:save")


@router.callback_query(DishForm.confirm, F.data == "ds:save")
async def cb_save(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    d = data["draft"]
    try:
        if data["mode"] == "add":
            saved = await api.create_dish(token, d["name"], d["price"], d["recipe"])
            done = "Блюдо добавлено."
        else:
            saved = await api.update_dish(
                token, data["dish_id"], d["name"], d["price"], d["recipe"], d.get("photoUrl"), d.get("category")
            )
            done = "Изменения сохранены."
    except Conflict:
        await state.update_data(back_to_confirm=True)
        await state.set_state(DishForm.name)
        await show(callback, f"Блюдо «{d['name']}» уже есть. Введите другое название.")
        return
    except NotFound:
        await state.clear()
        await show(callback, "Блюдо или продукт из состава не найдены, возможно их удалили. Список: /dishes")
        return
    except ValidationFailed as exc:
        await state.clear()
        await show(callback, "Не сохранено.\n" + api_error_text(exc) + "\nПопробуйте снова: /dishes")
        return
    await state.clear()
    saved = saved or d
    card = dish_card_text(saved["name"], saved["price"], saved["recipe"], data["products"], saved.get("category"))
    await show(callback, f"{done}\n\n{card}")


@router.callback_query(F.data.startswith("ds:d:"))
async def cb_delete_ask(
    callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, session: Session
) -> None:
    if not await access.check(callback, session, "catalog", "менять меню"):
        return
    dish_id = callback.data.removeprefix("ds:d:")
    dish = next((d for d in await api.list_dishes(token) if d["id"] == dish_id), None)
    if dish is None:
        await callback.answer("Блюдо не найдено, возможно его удалили.", show_alert=True)
        return
    await state.clear()
    await state.update_data(dish_id=dish_id, dish_name=dish["name"])
    await state.set_state(DishForm.delete_confirm)
    await show(callback, f"Удалить блюдо «{dish['name']}»?", confirm("Удалить", "ds:delok"))


@router.callback_query(DishForm.delete_confirm, F.data == "ds:delok")
async def cb_delete(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    name = data["dish_name"]
    await state.clear()
    try:
        await api.delete_dish(token, data["dish_id"])
    except NotFound:
        await show(callback, f"Блюдо «{name}» не найдено, возможно его уже удалили.")
        return
    except Conflict:
        await show(callback, f"По блюду «{name}» уже есть заказы, удалить нельзя.")
        return
    await show(callback, f"Блюдо «{name}» удалено.")
