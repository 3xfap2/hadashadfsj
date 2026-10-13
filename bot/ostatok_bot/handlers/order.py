"""/order: блюдо (со статусом из стоп-листа и скидкой горячего предложения) → порции → скидка →
ещё/готово → место → подтверждение. /close: закрыть стол. Оформлять заказы может только официант."""

from __future__ import annotations

import logging
import uuid
from datetime import datetime
from typing import Callable
from zoneinfo import ZoneInfo

from aiogram import F, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient, ApiError, ApiUnavailable, NotFound, ValidationFailed
from ..fmt import InputError, fmt_money, parse_positive, plural
from ..keyboards import cancel_row, confirm, paged, rows
from ..storage import Session, Storage, utcnow
from ..texts import api_error_text, order_result_text, stop_reason
from .common import SENDING, is_sending
from .pickers import page_from
from .util import ack, result_text, show

log = logging.getLogger(__name__)
router = Router(name="order")


class OrderForm(StatesGroup):
    dish = State()
    quantity = State()
    discount = State()
    more = State()
    place = State()
    confirm = State()
    sending = State()
    close_pick = State()
    close_confirm = State()


MAX_PORTIONS = 1000  # предел сервера на одну позицию заказа
PLACES_PER_ROW = 3
NO_PLACE = "or:pl:-"

Step = tuple[str, InlineKeyboardMarkup | None]

ORDER_RETRY = (
    "Сервер не ответил, заказ мог пройти. Нажмите «Повторить»: "
    "заказ уйдёт с тем же номером попытки и не задвоится."
)
ORDER_NOT_SENT = "Сервер недоступен, заказ не отправлен. Нажмите «Повторить», когда сервер ответит."
ORDER_UNKNOWN = (
    "Сервер не ответил, заказ мог пройти. Начните /order: бот сначала проверит, создан ли этот заказ."
)
ORDER_NOT_SENT_PLAIN = "Сервер недоступен, заказ не отправлен. Начните заново: /order"
PENDING_ORDER = "order"
RECENT_ORDERS = 50  # среди стольких последних заказов ищем тот, на который не получили ответ


def dish_cache(dishes: list[dict], stop_list: list[dict], offers: list[dict] | None = None) -> dict[str, dict]:
    status = {s["dishId"]: s for s in stop_list}
    discounts = {o["dishId"]: o["discount"] for o in offers or [] if o.get("discount")}
    result = {}
    for d in dishes:
        s = status.get(d["id"], {})
        discount = discounts.get(d["id"])
        result[d["id"]] = {
            "name": d["name"],
            "price": d["price"],
            "status": s.get("status", "Available"),
            "portions": s.get("portions"),
            "reason": stop_reason(s.get("reason"), s.get("limitingProductName")),
            "discount": discount["percent"] if discount else 0,
            "discountPrice": discount["price"] if discount else None,
        }
    return result


def dish_button(dish_id: str, dish: dict) -> tuple[str, str]:
    if dish["status"] == "Stop":
        return f"{dish['name']} — стоп", f"or:stop:{dish_id}"
    if dish["discount"]:
        return f"{dish['name']} — скидка {dish['discount']}%, {fmt_money(dish['discountPrice'])}", f"or:d:{dish_id}"
    if dish["status"] == "Low":
        return f"{dish['name']} — осталось {dish['portions']} порц.", f"or:d:{dish_id}"
    return f"{dish['name']} — {fmt_money(dish['price'])}", f"or:d:{dish_id}"


def items_lines(data: dict) -> list[str]:
    lines = []
    for item in data["items"]:
        dish = data["dishes"][item["dishId"]]
        line = f"{dish['name']} × {item['quantity']} — {fmt_money(dish['price'] * item['quantity'])}"
        if item.get("discount"):
            line += f", просим скидку {dish['discount']}%"
        lines.append(line)
    return lines


def total(data: dict) -> float:
    return sum(data["dishes"][i["dishId"]]["price"] * i["quantity"] for i in data["items"])


async def dish_step(state: FSMContext, data: dict, page: int = 0) -> Step:
    await state.set_state(OrderForm.dish)
    dishes = data["dishes"]
    ordered = sorted(dishes, key=lambda i: dishes[i]["name"].lower())
    keyboard = paged([dish_button(i, dishes[i]) for i in ordered], page, "or:pg", [cancel_row()])
    return "Выберите блюдо:", keyboard


async def more_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(OrderForm.more)
    text = "В заказе:\n" + "\n".join(items_lines(data))
    return text, rows([("Добавить ещё блюдо", "or:add")], [("Готово", "or:done")], cancel_row())


@router.message(Command("order"))
async def cmd_order(
    message: Message, state: FSMContext, api: ApiClient, token: str, session: Session, db: Storage
) -> None:
    if not await access.check(message, session, "orders", "оформлять заказы"):
        return
    if is_sending(await state.get_state()):
        await message.answer(SENDING)
        return
    note = await unfinished_order_note(api, db, token, message.chat.id)
    dishes = await api.list_dishes(token)
    stop_list = await api.get_stop_list(token)
    offers = await api.get_hot_offers(token)
    places = (await api.get_me(token)).get("places") or []
    await state.clear()
    if not dishes:
        await message.answer(note + "Блюд пока нет.")
        return
    data = await state.update_data(dishes=dish_cache(dishes, stop_list, offers), items=[], places=places)
    text, keyboard = await dish_step(state, data)
    await message.answer(note + "Новый заказ. Отменить: /cancel\n\n" + text, reply_markup=keyboard)


@router.callback_query(OrderForm.dish, F.data.startswith("or:pg:"))
async def cb_page(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await dish_step(state, await state.get_data(), page_from(callback.data))
    await show(callback, text, keyboard)


@router.callback_query(OrderForm.dish, F.data.startswith("or:stop:"))
async def cb_stopped(callback: CallbackQuery, state: FSMContext) -> None:
    dish = (await state.get_data())["dishes"].get(callback.data.removeprefix("or:stop:"))
    reason = f": {dish['reason']}" if dish else ""
    await callback.answer(f"Блюдо в стопе{reason}. Выбрать его нельзя.", show_alert=True)


@router.callback_query(OrderForm.dish, F.data.startswith("or:d:"))
async def cb_dish(callback: CallbackQuery, state: FSMContext) -> None:
    dish_id = callback.data.removeprefix("or:d:")
    dish = (await state.get_data())["dishes"].get(dish_id)
    if dish is None:
        await callback.answer("Блюдо не найдено.", show_alert=True)
        return
    await state.update_data(current=dish_id)
    await state.set_state(OrderForm.quantity)
    hint = f" Доступно примерно {dish['portions']}." if dish["status"] == "Low" else ""
    await show(callback, f"{dish['name']}. Сколько порций?{hint}")


@router.message(OrderForm.quantity)
async def got_quantity(message: Message, state: FSMContext) -> None:
    try:
        quantity = parse_positive(message.text, integer=True)
    except InputError as exc:
        await message.answer(f"{exc} Сколько порций?")
        return
    data = await state.get_data()
    same = next((i for i in data["items"] if i["dishId"] == data["current"]), None)
    already = same["quantity"] if same else 0
    if already + quantity > MAX_PORTIONS:
        hint = f" В заказе уже {already}." if already else ""
        await message.answer(f"Не больше {MAX_PORTIONS} порций одного блюда.{hint} Сколько порций?")
        return
    dish = data["dishes"][data["current"]]
    if dish["discount"] and not same:
        await state.update_data(pending=quantity)
        await state.set_state(OrderForm.discount)
        await message.answer(
            f"Продать «{dish['name']}» со скидкой {dish['discount']}%: {fmt_money(dish['discountPrice'])} "
            f"вместо {fmt_money(dish['price'])}? Сервер применит скидку, только если горячее предложение ещё действует.",
            reply_markup=rows([("Со скидкой", "or:ds:1"), ("Без скидки", "or:ds:0")], cancel_row()),
        )
        return
    text, keyboard = await add_item(state, quantity, discount=bool(same and same.get("discount")))
    await message.answer(text, reply_markup=keyboard)


async def add_item(state: FSMContext, quantity: int, discount: bool) -> Step:
    data = await state.get_data()
    items = [dict(i) for i in data["items"]]
    same = next((i for i in items if i["dishId"] == data["current"]), None)
    if same:
        same["quantity"] += quantity
    else:
        item = {"dishId": data["current"], "quantity": quantity}
        if discount:
            item["discount"] = True
        items.append(item)
    return await more_step(state, await state.update_data(items=items))


@router.callback_query(OrderForm.discount, F.data.startswith("or:ds:"))
async def cb_discount(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    text, keyboard = await add_item(state, data["pending"], discount=callback.data == "or:ds:1")
    await show(callback, text, keyboard)


@router.callback_query(OrderForm.more, F.data == "or:add")
async def cb_add(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await dish_step(state, await state.get_data())
    await show(callback, text, keyboard)


async def place_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(OrderForm.place)
    buttons = [(place, f"or:pl:{i}") for i, place in enumerate(data["places"])]
    grid = [buttons[i:i + PLACES_PER_ROW] for i in range(0, len(buttons), PLACES_PER_ROW)]
    return "Куда заказ?", rows(*grid, [("Без места", NO_PLACE)], cancel_row())


async def confirm_step(state: FSMContext, data: dict) -> Step:
    await state.set_state(OrderForm.confirm)
    lines = ["Заказ:", *items_lines(data)]
    if data.get("place"):
        lines.append(f"Место: {data['place']}")
    with_discount = any(i.get("discount") for i in data["items"])
    lines.append(f"Итого{' без скидки' if with_discount else ''}: {fmt_money(total(data))}")
    return "\n".join(lines) + "\n\nСоздать заказ?", confirm("Создать заказ", "or:send")


@router.callback_query(OrderForm.more, F.data == "or:done")
async def cb_done(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    step = place_step if data["places"] else confirm_step
    text, keyboard = await step(state, data)
    await show(callback, text, keyboard)


@router.callback_query(OrderForm.place, F.data.startswith("or:pl:"))
async def cb_place(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    place = None
    if callback.data != NO_PLACE:
        index = page_from(callback.data)
        if not 0 <= index < len(data["places"]):
            await callback.answer("Такого места нет, выберите другое.", show_alert=True)
            return
        place = data["places"][index]
    data = await state.update_data(place=place)
    text, keyboard = await confirm_step(state, data)
    await show(callback, text, keyboard)


async def unfinished_order_note(api: ApiClient, db: Storage, token: str, chat_id: int) -> str:
    """Что стало с заказом, ответ на который бот не получил (таймаут или перезапуск бота).
    Без этого официант оформил бы его второй раз с новым id — и продукты списались бы дважды."""
    pending = db.get_pending(chat_id, PENDING_ORDER)
    if not pending:
        return ""
    found = next((o for o in await api.list_orders(token, take=RECENT_ORDERS) if o.get("id") == pending["id"]), None)
    db.delete_pending(chat_id, PENDING_ORDER)
    if found:
        return "Прошлый заказ дошёл до сервера, повторять его не нужно.\n" + result_text(
            lambda: order_result_text(found), "Заказ обработан."
        ) + "\n\n"
    return "Прошлый заказ, на который сервер не ответил, не создан: среди последних заказов его нет.\n\n"


@router.callback_query(OrderForm.confirm, F.data == "or:send")
async def cb_send(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, db: Storage) -> None:
    data = await state.get_data()
    if not data.get("items"):
        # Данных заказа уже нет (диалог сбросили): отправлять нечего.
        await state.clear()
        await callback.answer("Этот заказ уже не отправить. Начните заново: /order", show_alert=True)
        return
    chat_id = callback.message.chat.id
    # Идентификатор попытки заказа: повтор с ним сервер не задвоит, а вернёт уже созданный заказ.
    order_id = data.get("order_id") or str(uuid.uuid4())
    await state.update_data(order_id=order_id)
    # До первого ожидания: второе нажатие уже не пройдёт фильтр «подтверждение».
    await state.set_state(OrderForm.sending)
    # id переживает перезапуск бота: следующий /order сначала проверит, не создан ли уже этот заказ.
    db.save_pending(chat_id, PENDING_ORDER, order_id, {"items": data["items"], "place": data.get("place")})
    settled = False  # сервер ответил определённо: заказ создан или точно не создан
    # Ответ на нажатие сразу: с повторами к засыпающему серверу запрос может идти минуты.
    await ack(callback, "Отправляю заказ…")
    try:
        order = await api.create_order(token, data["items"], data.get("place"), order_id)
        settled = True
    except ApiUnavailable as exc:
        log.warning("Заказ %s не создан: %s %r", order_id, exc, exc.body)
        if exc.from_app and not exc.unsure:
            # Ошибка самого сервера: повтор её не исправит.
            settled = True
            await state.clear()
            text = api_error_text(exc, f"Ошибка сервера (код {exc.status}).")
            await show(callback, "Заказ не создан.\n" + text + "\nНачать заново: /order")
            return
        settled = not exc.unsure
        if await state.get_state() != OrderForm.sending.state:
            # Пока шла отправка, диалог сбросили (другая команда): кнопки «Повторить» с прежними данными уже нет.
            await show(callback, ORDER_UNKNOWN if exc.unsure else ORDER_NOT_SENT_PLAIN)
            return
        await state.set_state(OrderForm.confirm)
        await show(callback, ORDER_RETRY if exc.unsure else ORDER_NOT_SENT, confirm("Повторить", "or:send"))
        return
    except (ValidationFailed, NotFound) as exc:
        settled = True
        await state.clear()
        fallback = "Какое-то блюдо не найдено, возможно его удалили." if isinstance(exc, NotFound) else None
        await show(callback, "Заказ не создан.\n" + api_error_text(exc, fallback) + "\nНачать заново: /order")
        return
    except ApiError:
        settled = True  # 401, 403 и другие отказы приложения: заказ не создан, ответит общий обработчик
        raise
    finally:
        if settled:
            db.delete_pending(chat_id, PENDING_ORDER)
        # Ошибка, которую ловят общие обработчики (401, 403…): не оставлять диалог в «отправляется».
        if await state.get_state() == OrderForm.sending.state:
            await state.set_state(OrderForm.confirm)
    # Пока шла отправка, сотрудник мог начать другой диалог: его не сбрасываем.
    if await state.get_state() == OrderForm.confirm.state:
        await state.clear()
    await show(callback, result_text(lambda: order_result_text(order), "Заказ отправлен, сервер ответил успехом."))


# Закрыть стол


def orders_word(n: int) -> str:
    return f"{n} {plural(n, 'заказ', 'заказа', 'заказов')}"


def start_of_day(now: datetime, tz: ZoneInfo) -> datetime:
    return now.astimezone(tz).replace(hour=0, minute=0, second=0, microsecond=0)


OTHER_TABLE = "После этого вы выбрали другой стол. Нажмите кнопку под последним вопросом."


@router.message(Command("close"))
async def cmd_close(
    message: Message, state: FSMContext, api: ApiClient, token: str, session: Session, tz: ZoneInfo,
    clock: Callable = utcnow,
) -> None:
    if not await access.check(message, session, "orders", "закрывать столы"):
        return
    # Открытые столы считает сервер по всем заказам, а не по последней странице списка.
    summary = await api.get_orders_summary(token, since=start_of_day(clock(), tz))
    tables = [{"place": t["place"], "total": t["total"]} for t in summary.get("tables") or []]
    await state.clear()
    if not tables:
        await message.answer("Открытых столов нет.")
        return
    await state.update_data(tables=tables)
    await state.set_state(OrderForm.close_pick)
    buttons = [[(f"{t['place']} — {fmt_money(t['total'])}", f"or:cl:{i}")] for i, t in enumerate(tables)]
    await message.answer("Какой стол закрыть?", reply_markup=rows(*buttons, cancel_row()))


@router.callback_query(OrderForm.close_pick, F.data.startswith("or:cl:"))
async def cb_close_pick(callback: CallbackQuery, state: FSMContext) -> None:
    tables = (await state.get_data())["tables"]
    index = page_from(callback.data)
    if not 0 <= index < len(tables):
        await callback.answer("Такого стола нет.", show_alert=True)
        return
    table = tables[index]
    # Метка вопроса — в самой кнопке: «Закрыть стол» под старым вопросом не закроет стол, выбранный позже.
    question = uuid.uuid4().hex[:8]
    await state.update_data(table=table, question=question)
    await state.set_state(OrderForm.close_confirm)
    await show(
        callback,
        f"Закрыть {table['place']}? Открыто заказов на {fmt_money(table['total'])}.",
        confirm("Закрыть стол", f"or:clok:{question}"),
    )


@router.callback_query(OrderForm.close_confirm, F.data.startswith("or:clok:"))
async def cb_close(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    if callback.data.removeprefix("or:clok:") != data.get("question"):
        await callback.answer(OTHER_TABLE, show_alert=True)
        return
    place = data["table"]["place"]
    await state.clear()
    try:
        closed = await api.close_table(token, place)
    except NotFound:
        await show(callback, f"У места «{place}» нет открытых заказов: возможно, его уже закрыли.")
        return
    except ValidationFailed as exc:
        await show(callback, api_error_text(exc))
        return
    await show(callback, f"{closed['place']} закрыт: {orders_word(closed['orders'])} на {fmt_money(closed['total'])}.")
