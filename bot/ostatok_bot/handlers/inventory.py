"""/inventory: продукт → сколько по факту → ещё продукт или «Провести» → расхождения и сумма.
Передаются только пересчитанные продукты. По учёту — все партии продукта, вместе с просроченными
несписанными: так же считает сервер, поэтому и по факту нужно вводить вместе с просрочкой."""

from __future__ import annotations

from aiogram import F, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

from .. import access
from ..api import ApiClient, NotFound, ValidationFailed
from ..fmt import InputError, fmt_money, fmt_qty, parse_number
from ..keyboards import cancel_row, rows
from ..storage import Session
from ..texts import api_error_text
from .pickers import page_from, product_cache, product_picker
from .util import show

router = Router(name="inventory")

Step = tuple[str, InlineKeyboardMarkup | None]


class InventoryForm(StatesGroup):
    product = State()
    actual = State()
    more = State()


def signed_qty(value: float, unit: str) -> str:
    sign = "+" if value > 0 else "−" if value < 0 else ""
    return sign + fmt_qty(abs(value), unit)


def accounted(batches: list[dict]) -> dict[str, dict]:
    """По учёту на продукт: весь остаток партий и сколько из него просрочено."""
    result: dict[str, dict] = {}
    for b in batches:
        row = result.setdefault(b["productId"], {"total": 0.0, "expired": 0.0})
        row["total"] = round(row["total"] + b["remaining"], 3)
        if b.get("status") == "Expired":
            row["expired"] = round(row["expired"] + b["remaining"], 3)
    return result


def counted_text(data: dict) -> str:
    lines = ["Пересчитано:"]
    for n, item in enumerate(data["items"], 1):
        product = data["products"][item["productId"]]
        was = data["accounted"].get(item["productId"], {}).get("total", 0)
        diff = round(item["actual"] - was, 3)
        tail = "совпадает" if diff == 0 else signed_qty(diff, product["unit"])
        lines.append(
            f"{n}. {product['name']}: по учёту {fmt_qty(was, product['unit'])}, "
            f"по факту {fmt_qty(item['actual'], product['unit'])} ({tail})"
        )
    lines.append("\nОстатки изменятся под факт. Провести инвентаризацию?")
    return "\n".join(lines)


def result_text(result: dict) -> str:
    changed = result.get("rows") or []
    if not changed:
        return "Инвентаризация проведена: расхождений нет, по факту всё совпало с учётом."
    lines = ["Инвентаризация проведена. Расхождения:"]
    for r in changed:
        kind = "недостача" if r["difference"] < 0 else "излишек"
        lines.append(
            f"{r['productName']}: по учёту {fmt_qty(r['accounted'], r['unit'])}, по факту {fmt_qty(r['actual'], r['unit'])}, "
            f"{kind} {fmt_qty(abs(r['difference']), r['unit'])} на {fmt_money(abs(r['value']))}"
        )
    lines.append("")
    if result.get("shortage"):
        lines.append(f"Недостача: {fmt_money(result['shortage'])}")
    if result.get("surplus"):
        lines.append(f"Излишек: {fmt_money(result['surplus'])}")
    lines.append("В журнале: /journal")
    return "\n".join(lines)


async def product_step(state: FSMContext, data: dict, page: int = 0) -> Step:
    await state.set_state(InventoryForm.product)
    head = "Какой продукт пересчитать?"
    if data.get("items"):
        head = f"Пересчитано продуктов: {len(data['items'])}. Какой ещё?"
    return head, product_picker(data["products"], page, "iv:p", "iv:pg")


@router.message(Command("inventory"))
async def cmd_inventory(message: Message, state: FSMContext, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(message, session, "stock", "проводить инвентаризацию"):
        return
    await state.clear()
    products = product_cache(await api.list_products(token))
    if not products:
        await message.answer("В справочнике нет продуктов: пересчитывать нечего.")
        return
    data = await state.update_data(products=products, accounted=accounted(await api.get_batches(token)), items=[])
    text, keyboard = await product_step(state, data)
    await message.answer(text, reply_markup=keyboard)


@router.callback_query(InventoryForm.product, F.data.startswith("iv:pg:"))
async def cb_page(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.get_data(), page_from(callback.data))
    await show(callback, text, keyboard)


@router.callback_query(InventoryForm.product, F.data.startswith("iv:p:"))
async def cb_product(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    product_id = callback.data.removeprefix("iv:p:")
    product = data["products"].get(product_id)
    if product is None:
        await callback.answer("Продукт не найден, выберите другой.", show_alert=True)
        return
    await state.update_data(product_id=product_id)
    await state.set_state(InventoryForm.actual)
    was = data["accounted"].get(product_id, {"total": 0, "expired": 0})
    unit = product["unit"]
    line = f"{product['name']}: по учёту {fmt_qty(was['total'], unit)}"
    if was["expired"]:
        line += f", из них просрочено {fmt_qty(was['expired'], unit)}"
    await show(callback, f"{line}.\nСколько по факту, вместе с просроченным? Введите число.", rows(cancel_row()))


@router.message(InventoryForm.actual)
async def got_actual(message: Message, state: FSMContext) -> None:
    try:
        actual = parse_number(message.text)
        if actual < 0:
            raise InputError("Остаток не может быть отрицательным.")
    except InputError as exc:
        await message.answer(f"{exc} Сколько по факту?")
        return
    data = await state.get_data()
    # Повторный пересчёт того же продукта заменяет прежнее число.
    items = [i for i in data["items"] if i["productId"] != data["product_id"]]
    items.append({"productId": data["product_id"], "actual": round(actual, 3)})
    data = await state.update_data(items=items)
    await state.set_state(InventoryForm.more)
    await message.answer(
        counted_text(data),
        reply_markup=rows([("Провести инвентаризацию", "iv:send")], [("Ещё продукт", "iv:add")], cancel_row()),
    )


@router.callback_query(InventoryForm.more, F.data == "iv:add")
async def cb_add(callback: CallbackQuery, state: FSMContext) -> None:
    text, keyboard = await product_step(state, await state.get_data())
    await show(callback, text, keyboard)


@router.callback_query(InventoryForm.more, F.data == "iv:send")
async def cb_send(callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str) -> None:
    data = await state.get_data()
    # Диалог сбрасывается только после определённого ответа сервера. Если сервер недоступен, пересчёт
    # и кнопка «Провести» остаются: повтор безопасен, инвентаризация выставляет остаток под факт.
    try:
        result = await api.inventory(token, data["items"])
    except (ValidationFailed, NotFound) as exc:
        await clear_if_still_here(state)
        fallback = "Какой-то продукт не найден, возможно его удалили." if isinstance(exc, NotFound) else None
        await show(callback, "Инвентаризация не проведена, остатки не изменились.\n"
                   + api_error_text(exc, fallback) + "\nНачать заново: /inventory")
        return
    await clear_if_still_here(state)
    await show(callback, result_text(result))


async def clear_if_still_here(state: FSMContext) -> None:
    """Пока шёл запрос, сотрудник мог начать другой диалог: его не сбрасываем."""
    if await state.get_state() == InventoryForm.more.state:
        await state.clear()
