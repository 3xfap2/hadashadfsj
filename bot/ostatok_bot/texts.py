"""Тексты сообщений бота, собранные из ответов API."""

from __future__ import annotations

from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

from .api import ApiError
from .fmt import fmt_dt, fmt_hours_left, fmt_money, fmt_number, fmt_qty, parse_dt, plural, unit_label

LOGIN_REQUIRED = "Войдите через /login"
SESSION_EXPIRED = (
    "Сессия закончилась: срок входа истёк или доступ изменили (роль, пароль, сотрудника удалили). "
    "Войдите заново: /login"
)
FORBIDDEN = "Для вашей роли это действие недоступно."
API_DOWN = "Сервер «Остатка» сейчас недоступен. Попробуйте позже."
API_NO_ANSWER = (
    "Сервер «Остатка» не ответил вовремя. Действие могло выполниться: "
    "проверьте результат, прежде чем повторять."
)

EXPIRING_WINDOW_HOURS = 24
JOURNAL_SIZE = 15
LOSSES_DAYS = 7
LOSSES_TOP = 3
STOPCOST_DAYS = 7
STOPCOST_TOP = 5

MOVEMENT_TYPES = {
    "Supply": "Поставка",
    "Order": "Заказ",
    "WriteOff": "Списание",
    "PrepUse": "На заготовку",
    "PrepMade": "Заготовка",
    "Shortage": "Недостача",
    "Surplus": "Излишек",
    "Opened": "Вскрытие",
}
INCOMING = {"Supply", "PrepMade", "Surplus"}
# Вскрытие не меняет остаток: часть партии переходит во вскрытую, знак не ставится.
NEUTRAL = {"Opened"}


def stop_reason(reason: str | None, product: str | None) -> str:
    product = product or "продукт"
    if reason == "Expired":
        return f"просрочен продукт «{product}»"
    if reason == "OutOfStock":
        return f"закончился продукт «{product}»"
    if reason == "NotEnough":
        return f"продукта «{product}» меньше чем на порцию"
    if reason == "Manual":
        return "снято с продажи вручную"
    return "нет продуктов"


def api_error_text(exc: ApiError, fallback: str | None = None) -> str:
    """Текст ошибки от сервера: поле errors (400), иначе detail, иначе запасное сообщение."""
    messages = exc.messages()
    if messages:
        return "\n".join(messages)
    if exc.status == 429 and not exc.detail:
        return fallback or "Слишком много запросов к серверу. Подождите минуту и повторите."
    return exc.detail or fallback or f"API вернул ошибку (код {exc.status})."


def stop_forecast(value: str, now: datetime, tz: ZoneInfo) -> str:
    """Когда блюдо уйдёт в стоп: «в 14:30», «завтра в 02:00» или «12.10 в 02:00»."""
    at = parse_dt(value).astimezone(tz)
    today = now.astimezone(tz).date()
    time = at.strftime("%H:%M")
    if at.date() <= today:
        return f"в {time}"
    if at.date() == today + timedelta(days=1):
        return f"завтра в {time}"
    return f"{at.strftime('%d.%m')} в {time}"


def stop_list_text(items: list[dict], now: datetime, tz: ZoneInfo) -> str:
    stopped = [i for i in items if i["status"] == "Stop"]
    low = [i for i in items if i["status"] == "Low"]
    if not stopped and not low:
        return "Все блюда доступны."
    parts = []
    if stopped:
        lines = [f"{i['dishName']} — {stop_reason(i.get('reason'), i.get('limitingProductName'))}" for i in stopped]
        parts.append("В стопе:\n" + "\n".join(lines))
    if low:
        lines = []
        for i in low:
            line = f"{i['dishName']} — {i['portions']} порц."
            if i.get("stopForecastAt"):
                line += f", стоп примерно {stop_forecast(i['stopForecastAt'], now, tz)}"
            lines.append(line)
        parts.append("Заканчиваются:\n" + "\n".join(lines))
    return "\n\n".join(parts)


def batch_amount(batch: dict) -> str:
    """«0,9 л на 342 ₽»; без суммы, если API её не прислал."""
    amount = fmt_qty(batch["remaining"], batch["unit"])
    if batch.get("value") is not None:
        amount += f" на {fmt_money(batch['value'])}"
    return amount


def can_open(batch: dict) -> bool:
    """Вскрыть можно, если у продукта есть срок после вскрытия, партия ещё закрыта, не просрочена и не пуста."""
    return (
        batch.get("openedShelfLifeHours") is not None
        and batch.get("openedAt") is None
        and batch.get("status") != "Expired"
        and (batch.get("remaining") or 0) > 0
    )


def opened_mark(batch: dict) -> str:
    return ", вскрыта" if batch.get("openedAt") else ""


def batch_line(batch: dict) -> str:
    return (
        f"{batch['productName']} — {batch_amount(batch)}, "
        f"{fmt_hours_left(batch['hoursLeft'])}, поставка {batch['supplyNumber']}{opened_mark(batch)}"
    )


def product_batches_text(batches: list[dict], tz: ZoneInfo) -> str:
    """Партии одного продукта для карточки /stock: срок, поставка, вскрыта ли."""
    if not batches:
        return "Партий с остатком нет."
    lines = ["Партии:"]
    for b in batches:
        state = "просрочена" if b.get("status") == "Expired" else f"годна до {fmt_dt(b['expiresAt'], tz)}"
        lines.append(f"• {fmt_qty(b['remaining'], b['unit'])}, {state}, поставка {b['supplyNumber']}{opened_mark(b)}")
    return "\n".join(lines)


def expiring_batches(batches: list[dict]) -> list[dict]:
    return [
        b for b in batches
        if b["status"] == "Expired" or b["hoursLeft"] <= EXPIRING_WINDOW_HOURS
    ]


def expiring_text(batches: list[dict]) -> str:
    if not batches:
        return "Нет партий, которые истекают в ближайшие сутки."
    expired = [b for b in batches if b["status"] == "Expired"]
    soon = [b for b in batches if b["status"] != "Expired"]
    parts = []
    if expired:
        parts.append("Просрочены:\n" + "\n".join(batch_line(b) for b in expired))
    if soon:
        parts.append("Истекают в ближайшие сутки:\n" + "\n".join(batch_line(b) for b in soon))
    return "\n\n".join(parts)


def write_off_question(batch: dict) -> str:
    return (
        f"Списать {batch_amount(batch)}: {batch['productName']} "
        f"(поставка {batch['supplyNumber']})?"
    )


def stock_item_text(item: dict, tz: ZoneInfo) -> str:
    unit = item["unit"]
    lines = [
        item["productName"],
        # Стоимость сервер присылает только ролям, которым видны закупочные цены.
        f"Годный остаток: {fmt_qty(item['fresh'], unit)}"
        + (f" на {fmt_money(item['freshValue'])}" if item.get("freshValue") is not None else ""),
    ]
    if item["expired"]:
        lines.append(f"Просрочено: {fmt_qty(item['expired'], unit)}")
    lines.append(f"Партий: {item['batches']}")
    if item.get("nearestExpiry"):
        lines.append(f"Ближайший срок: {fmt_dt(item['nearestExpiry'], tz)}")
    return "\n".join(lines)


def stock_summary_text(items: list[dict]) -> str:
    # API присылает все продукты справочника, в том числе без остатка.
    ordered = sorted(items, key=lambda i: i["productName"].lower())
    present = [i for i in ordered if i["fresh"] or i["expired"]]
    missing = [i["productName"] for i in ordered if not (i["fresh"] or i["expired"])]
    if not present:
        return "Склад пуст."
    lines = []
    for item in present:
        line = f"{item['productName']} — {fmt_qty(item['fresh'], item['unit'])}"
        if item["expired"]:
            line += f", просрочено {fmt_qty(item['expired'], item['unit'])}"
        lines.append(line)
    values = [i["freshValue"] for i in present if i.get("freshValue") is not None]
    text = "Остатки:\n" + "\n".join(lines)
    if values:
        text += f"\n\nГодных продуктов на {fmt_money(sum(values))}"
    if missing:
        text += "\nНет на складе: " + ", ".join(missing)
    return text


def journal_text(movements: list[dict], tz: ZoneInfo) -> str:
    if not movements:
        return "Журнал пуст."
    latest = sorted(movements, key=lambda m: m["at"], reverse=True)[:JOURNAL_SIZE]
    lines = []
    for m in latest:
        kind = MOVEMENT_TYPES.get(m["type"], m["type"])
        sign = "+" if m["type"] in INCOMING else "" if m["type"] in NEUTRAL else "−"
        line = f"{fmt_dt(m['at'], tz)} {kind}: {m['productName']} {sign}{fmt_qty(abs(m['quantity']), m['unit'])}"
        if m.get("note"):
            line += f" ({m['note']})"
        if m.get("actor"):
            line += f", {m['actor']}"
        lines.append(line)
    return "Последние операции:\n" + "\n".join(lines)


def hot_offers_text(offers: list[dict] | None) -> str:
    if offers is None:
        return "Горячие предложения скоро появятся."
    if not offers:
        return "Горячих предложений сейчас нет."
    # Объяснение в reason собирает сервер, бот его не переписывает.
    blocks = []
    for o in offers:
        n = o["portions"]
        pinned = " (закреплено)" if o.get("pinned") else ""
        head = f"{o['dishName']}{pinned} — продать {n} {plural(n, 'порцию', 'порции', 'порций')}"
        if o.get("valueAtRisk") is not None:
            head += f", под угрозой списания {fmt_money(o['valueAtRisk'])}"
        lines = [head]
        if o.get("reason"):
            lines.append(o["reason"])
        discount = o.get("discount")
        if discount:
            # Объяснение скидки сервер присылает целиком, вместе с процентом и ценой.
            lines.append(
                discount.get("reason")
                or f"Скидка {discount['percent']}%: {fmt_money(discount['price'])} вместо {fmt_money(o['price'])}"
            )
        blocks.append("\n".join(lines))
    return "Горячие предложения:\n\n" + "\n\n".join(blocks)


def hot_plan_text(plan: dict | None) -> str:
    """Итог плана спасения из GET /api/hot-offers/plan. Суммы сервер присылает только ролям, которым видны цены."""
    if not plan:
        return ""
    n = plan.get("portions") or 0
    lines = []
    if n:
        line = f"План спасения: продать {n} {plural(n, 'порцию', 'порции', 'порций')}"
        if plan.get("plannedValue") is not None and plan.get("atRiskValue") is not None:
            line += (
                f" — так в блюда уйдёт {fmt_money(plan['plannedValue'])} "
                f"из {fmt_money(plan['atRiskValue'])} истекающих продуктов"
            )
        lines.append(line + ".")
    leftovers = plan.get("leftovers") or []
    if leftovers:
        lines.append("Не пристроено ни в одно блюдо:")
        for left in leftovers:
            line = f"— {left['productName']}: {fmt_qty(left['quantity'], left['unit'])}, {fmt_hours_left(left['hoursLeft'])}"
            if left.get("value") is not None:
                line += f", {fmt_money(left['value'])}"
            lines.append(line)
    return "\n".join(lines)


def purchase_text(suggestions: list[dict]) -> str:
    if not suggestions:
        return "Заказывать сейчас ничего не нужно."
    blocks = []
    for item in suggestions:
        head = f"{item['productName']} — заказать {fmt_qty(item['quantity'], item['unit'])}"
        if item.get("sum"):
            head += f" на {fmt_money(item['sum'])}"
        lines = [head]
        if item.get("supplier"):
            lines.append(f"Поставщик: {item['supplier']}")
        if item.get("reason"):
            lines.append(item["reason"])
        blocks.append("\n".join(lines))
    return "Что заказать:\n\n" + "\n\n".join(blocks)


def losses_text(report: dict) -> str:
    days = report.get("days", LOSSES_DAYS)
    if not report.get("total"):
        return f"За {days} дн. списаний не было."
    lines = [f"Списано за {days} дн.: {fmt_money(report['total'])}"]
    top = sorted(report.get("byProduct", []), key=lambda p: p["value"], reverse=True)[:LOSSES_TOP]
    if top:
        lines += ["", "Больше всего потерь:"]
        lines += [
            f"{n}. {p['productName']} — {fmt_qty(p['quantity'], p['unit'])}, {fmt_money(p['value'])}"
            for n, p in enumerate(top, 1)
        ]
    return "\n".join(lines)


def stop_cost_text(report: dict) -> str:
    days = report.get("days", STOPCOST_DAYS)
    dishes = report.get("dishes") or []
    if not dishes:
        return f"За {days} дн. блюда в стоп-лист не попадали."
    lines = [f"Недополучено из-за стоп-листа за {days} дн.: {fmt_money(report.get('total', 0))}", ""]
    for n, d in enumerate(dishes[:STOPCOST_TOP], 1):
        lines.append(
            f"{n}. {d['dishName']} — {fmt_number(d['stopHours'], 1)} ч в стопе, "
            f"не продано {fmt_number(d['lostPortions'], 1)} порц., {fmt_money(d['lostRevenue'])}"
        )
    return "\n".join(lines)


def preps_text(preps: list[dict]) -> str:
    if not preps:
        return "Заготовок нет: ни у одного продукта не задан состав."
    blocks = []
    for p in preps:
        unit = p["unit"]
        lines = [f"{p['productName']} — есть {fmt_qty(p['fresh'], unit)}"]
        if p.get("suggested"):
            lines.append(f"Приготовить: {fmt_qty(p['suggested'], unit)}" + (f". {p['reason']}" if p.get("reason") else ""))
        lines.append(f"Из сырья можно сделать до {fmt_qty(p['maxQuantity'], unit)}")
        blocks.append("\n".join(lines))
    return "Заготовки:\n\n" + "\n\n".join(blocks)


def prep_result_text(result: dict, name: str, unit: str, tz: ZoneInfo) -> str:
    shortages = result.get("shortages") or []
    if shortages:
        lines = [
            f"{s['productName']}: нужно {fmt_qty(s['required'], s['unit'])}, есть {fmt_qty(s['available'], s['unit'])}"
            for s in shortages
        ]
        return f"Не хватает сырья на «{name}», ничего не списано:\n" + "\n".join(lines)
    return (
        f"Готово: {name}, {fmt_qty(result['quantity'], unit)}. Партия {result['supplyNumber']}, "
        f"годна до {fmt_dt(result['expiresAt'], tz)}."
    )


def product_line(p: dict) -> str:
    unit = unit_label(p["unit"])
    line = f"{p['name']} — {unit}, хранение {p['shelfLifeHours']} ч"
    if p.get("cost") is not None:
        line += f", {fmt_money(p['cost'])} за {unit}"
    return line + (", заготовка" if p.get("prepRecipe") else "")


def products_text(products: list[dict]) -> str:
    if not products:
        return "Продуктов пока нет."
    ordered = sorted(products, key=lambda p: p["name"].lower())
    return "Продукты:\n" + "\n".join(product_line(p) for p in ordered)


def recipe_lines(recipe: list[dict], products: dict[str, dict]) -> list[str]:
    lines = []
    for r in recipe:
        product = products.get(r["productId"])
        if product:
            lines.append(f"— {product['name']}: {fmt_qty(r['quantity'], product['unit'])}")
        else:
            lines.append(f"— неизвестный продукт: {fmt_number(r['quantity'])}")
    return lines


def dish_card_text(
    name: str,
    price: float,
    recipe: list[dict],
    products: dict[str, dict],
    category: str | None = None,
    stopped: bool = False,
) -> str:
    lines = [name]
    if stopped:
        lines.append("Снято с продажи вручную")
    if category:
        lines.append(f"Раздел меню: {category}")
    lines += [f"Цена: {fmt_money(price)}", "Состав:"]
    lines += recipe_lines(recipe, products) or ["— пусто"]
    return "\n".join(lines)


def order_item_line(item: dict) -> str:
    line = f"{item['dishName']} × {item['quantity']}"
    if item.get("discountPercent") and item.get("discountQuantity"):
        line += f", скидка {item['discountPercent']}% на {item['discountQuantity']} порц."
    return line


def order_result_text(order: dict) -> str:
    items = "\n".join(order_item_line(i) for i in order.get("items", []))
    if order.get("status") == "Accepted":
        place = f", {order['place']}" if order.get("place") else ""
        text = f"Заказ №{order['number']} принят{place}.\n{items}"
        if order.get("total") is not None:
            text += f"\nСумма: {fmt_money(order['total'])}"
        write_offs = [
            f"{w['productName']} — {fmt_qty(w['quantity'], w['unit'])}, поставка {w['supplyNumber']}"
            for w in order.get("writeOffs") or []
        ]
        if write_offs:
            text += "\n\nСписано:\n" + "\n".join(write_offs)
        return text
    lines = [
        f"{s['productName']}: нужно {fmt_qty(s['required'], s['unit'])}, есть {fmt_qty(s['available'], s['unit'])}"
        for s in order.get("shortages", [])
    ]
    head = f"Заказ №{order['number']} отклонён" if order.get("number") else "Заказ отклонён"
    return f"{head}, не хватает продуктов:\n" + "\n".join(lines)


def supply_result_text(supply: dict, tz: ZoneInfo) -> str:
    lines = [
        f"{i['productName']} — {fmt_qty(i['quantity'], i['unit'])}, годен до {fmt_dt(i['expiresAt'], tz)}"
        for i in supply.get("items", [])
    ]
    head = f"Поставка {supply['number']} принята"
    if supply.get("supplier"):
        head += f", поставщик {supply['supplier']}"
    if supply.get("total"):
        head += f", на {fmt_money(supply['total'])}"
    return f"{head}. Партии:\n" + "\n".join(lines)
