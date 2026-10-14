import pytest

from ostatok_bot.fmt import (
    InputError,
    fmt_dt,
    fmt_hours_left,
    fmt_money,
    fmt_qty,
    parse_money,
    parse_number,
    parse_positive,
    plural,
)
from ostatok_bot import texts

from .conftest import CREAM, NOW, SALMON, TARTARE, TZ, batch, stop_item


@pytest.mark.parametrize(
    "value, unit, expected",
    [
        (0.7, "Kg", "0,7 кг"),
        (0.70, "L", "0,7 л"),
        (2.0, "Pcs", "2 шт"),
        (2.5, "L", "2,5 л"),
        (0.44, "Kg", "0,44 кг"),
        (0.1 + 0.2, "Kg", "0,3 кг"),
        (0.0005, "Kg", "0,001 кг"),
        (0, "Kg", "0 кг"),
        (12500, "Pcs", "12 500 шт"),
    ],
)
def test_fmt_qty(value, unit, expected):
    assert fmt_qty(value, unit) == expected


@pytest.mark.parametrize(
    "value, expected",
    [(1190, "1 190 ₽"), (1190.4, "1 190 ₽"), (1190.5, "1 191 ₽"), (6240.0, "6 240 ₽"),
     (0, "0 ₽"), (999, "999 ₽"), (1234567, "1 234 567 ₽")],
)
def test_fmt_money(value, expected):
    assert fmt_money(value) == expected


@pytest.mark.parametrize(
    "hours, expected",
    [
        (5.0, "осталось 5 ч"),
        (5.9, "осталось 5 ч"),
        (0.5, "осталось 30 мин"),
        (23, "осталось 23 ч"),
        (50, "осталось 2 сут 2 ч"),
        (72, "осталось 3 сут"),
        (-3.0, "просрочено 3 ч назад"),
        (-0.25, "просрочено 15 мин назад"),
        (0, "срок истекает сейчас"),
    ],
)
def test_fmt_hours_left(hours, expected):
    assert fmt_hours_left(hours) == expected


def test_fmt_dt_uses_timezone():
    assert fmt_dt("2026-10-10T06:00:00+00:00", TZ) == "10.10 09:00"
    assert fmt_dt("2026-10-10T06:00:00Z", TZ) == "10.10 09:00"


@pytest.mark.parametrize("text, expected", [("2,5", 2.5), ("2.5", 2.5), (" 3 ", 3), ("0,44", 0.44), ("1 000", 1000), ("-1", -1)])
def test_parse_number(text, expected):
    assert parse_number(text) == expected


@pytest.mark.parametrize("text", ["", "   ", "abc", "1,2,3", "1.2.3", "2,5кг", "nan", "inf", None])
def test_parse_number_rejects(text):
    with pytest.raises(InputError):
        parse_number(text)


def test_parse_number_error_names_the_input():
    with pytest.raises(InputError, match="«abc» не похоже на число"):
        parse_number("abc")


def test_parse_positive():
    assert parse_positive("0,7") == 0.7
    assert parse_positive("48", integer=True) == 48
    assert isinstance(parse_positive("48.0", integer=True), int)
    with pytest.raises(InputError, match="больше нуля"):
        parse_positive("0")
    with pytest.raises(InputError, match="больше нуля"):
        parse_positive("-2")
    with pytest.raises(InputError, match="целое"):
        parse_positive("1,5", integer=True)


def test_parse_money():
    assert parse_money("1190") == 1190
    assert parse_money("0") == 0
    with pytest.raises(InputError, match="отрицательной"):
        parse_money("-5")


# Сообщения


def test_stop_list_text():
    items = [
        stop_item(),
        stop_item({"id": "d2", "name": "Паста", "price": 500}, reason="OutOfStock", product=CREAM),
        stop_item({"id": "d3", "name": "Омлет", "price": 300}, status="Low", portions=3,
                  forecast="2026-10-09T15:30:00+00:00"),
        stop_item({"id": "d5", "name": "Блины", "price": 250}, status="Low", portions=2),
        stop_item({"id": "d4", "name": "Салат", "price": 300}, status="Available", portions=20),
    ]
    assert texts.stop_list_text(items, NOW, TZ) == (
        "В стопе:\n"
        "Тартар из лосося — просрочен продукт «Лосось охлаждённый»\n"
        "Паста — закончился продукт «Сливки 33%»\n\n"
        "Заканчиваются:\n"
        "Омлет — 3 порц., стоп примерно в 18:30\n"
        "Блины — 2 порц."
    )
    assert texts.stop_list_text([stop_item(status="Available", portions=5)], NOW, TZ) == "Все блюда доступны."


@pytest.mark.parametrize(
    "forecast, expected",
    [
        ("2026-10-09T15:30:00+00:00", "в 18:30"),
        ("2026-10-09T11:00:00+00:00", "в 14:00"),
        ("2026-10-09T21:30:00+00:00", "завтра в 00:30"),  # по UTC ещё 9-е, по Москве уже 10-е
        ("2026-10-10T20:59:00+00:00", "завтра в 23:59"),
        ("2026-10-12T06:00:00+00:00", "12.10 в 09:00"),
    ],
)
def test_stop_forecast(forecast, expected):
    assert texts.stop_forecast(forecast, NOW, TZ) == expected


@pytest.mark.parametrize(
    "n, expected",
    [(1, "порцию"), (2, "порции"), (4, "порции"), (5, "порций"), (8, "порций"), (11, "порций"),
     (12, "порций"), (21, "порцию"), (22, "порции"), (111, "порций")],
)
def test_plural(n, expected):
    assert plural(n, "порцию", "порции", "порций") == expected


def test_stop_reason_not_enough():
    assert texts.stop_reason("NotEnough", "Яйцо") == "продукта «Яйцо» меньше чем на порцию"


def test_expiring_text_and_filter():
    batches = [
        batch("b1", "Expired", -3),
        batch("b2", "ExpiringSoon", 5, product=SALMON, remaining=0.26, supply="П-0414", value=494.0),
        batch("b3", "Fresh", 20, product=SALMON, supply="П-0415", value=None),
        batch("b4", "Fresh", 60, product=SALMON, supply="П-0416"),
    ]
    selected = texts.expiring_batches(batches)
    assert [b["id"] for b in selected] == ["b1", "b2", "b3"]
    assert texts.expiring_text(selected) == (
        "Под списание (срок истёк):\n"
        "Сливки 33% — 0,9 л на 342 ₽, просрочено 3 ч назад, поставка П-0413\n\n"
        "Истекают в ближайшие сутки:\n"
        "Лосось охлаждённый — 0,26 кг на 494 ₽, осталось 5 ч, поставка П-0414\n"
        "Лосось охлаждённый — 0,9 кг, осталось 20 ч, поставка П-0415"
    )


def test_write_off_question_shows_value():
    assert texts.write_off_question(batch(value=341.6)) == "Списать 0,9 л на 342 ₽: Сливки 33% (поставка П-0413)?"


HOT_OFFER = {
    "dishId": "d-tomyum", "dishName": "Том ям", "photoUrl": "/dishes/tom-yum.jpg", "price": 690,
    "score": 1449.71, "portions": 8, "availablePortions": 8, "valueAtRisk": 1992.0,
    "reason": "Основа том ям: 2,4 л истекает через 5 ч. Продайте 8 порций — это 1 992 ₽ продуктов, "
              "которые иначе уйдут в списание. Других блюд с этим продуктом в продаже нет.",
    "ingredients": [{"productId": "p1", "productName": "Основа том ям", "unit": "L", "atRiskQuantity": 2.4,
                     "nearestQuantity": 2.4, "hoursLeft": 5.2, "usableQuantity": 2.4, "valueAtRisk": 840.0,
                     "alternativeDishes": 1, "score": 749.0}],
}


def test_hot_offers_text_uses_server_reason_and_keeps_order():
    second = {**HOT_OFFER, "dishName": "Карбонара", "portions": 1, "valueAtRisk": 120.4, "reason": "Сливки истекают."}
    assert texts.hot_offers_text([HOT_OFFER, second]) == (
        "Горячие предложения:\n\n"
        "Том ям — продать 8 порций, под угрозой списания 1 992 ₽\n" + HOT_OFFER["reason"] + "\n\n"
        "Карбонара — продать 1 порцию, под угрозой списания 120 ₽\n"
        "Сливки истекают."
    )
    assert texts.hot_offers_text([]) == "Горячих предложений сейчас нет."


def test_hot_offer_added_by_manager_has_no_portions_line():
    added = {**HOT_OFFER, "dishName": "Стейк", "portions": 0, "valueAtRisk": 0, "ingredients": [], "discount": None,
             "manual": True, "note": "Повар Иван на гриле", "reason": "Добавлено управляющим: Повар Иван на гриле"}
    assert texts.hot_offers_text([added]) == "Горячие предложения:\n\nСтейк\nДобавлено управляющим: Повар Иван на гриле"


def test_purchase_text():
    item = {"productId": "p-salmon", "productName": "Лосось охлаждённый", "unit": "Kg", "quantity": 5.2,
            "fresh": 2.1, "perDay": 2.4, "hoursCovered": 20.0,
            "reason": "При расходе 2,4 кг в день остатка хватит на 20 ч."}
    assert texts.purchase_text([item]) == (
        "Что заказать:\n\nЛосось охлаждённый — заказать 5,2 кг\nПри расходе 2,4 кг в день остатка хватит на 20 ч."
    )
    assert texts.purchase_text([]) == "Заказывать сейчас ничего не нужно."


def test_losses_text_top_three_by_value():
    report = {
        "days": 7, "total": 12340.5,
        "byDay": [{"date": "2026-10-09", "value": 12340.5}],
        "byProduct": [
            {"productName": "Яйцо", "unit": "Pcs", "quantity": 30, "value": 360},
            {"productName": "Лосось охлаждённый", "unit": "Kg", "quantity": 3.2, "value": 6080},
            {"productName": "Сливки 33%", "unit": "L", "quantity": 4.5, "value": 1710},
            {"productName": "Основа том ям", "unit": "L", "quantity": 2.4, "value": 4190.5},
        ],
        "byReason": [{"reason": "Expired", "value": 12340.5}],
    }
    assert texts.losses_text(report) == (
        "Списано за 7 дн.: 12 341 ₽\n\n"
        "Больше всего потерь:\n"
        "1. Лосось охлаждённый — 3,2 кг, 6 080 ₽\n"
        "2. Основа том ям — 2,4 л, 4 191 ₽\n"
        "3. Сливки 33% — 4,5 л, 1 710 ₽"
    )
    assert texts.losses_text({"days": 7, "total": 0, "byDay": [], "byProduct": [], "byReason": []}) == (
        "За 7 дн. списаний не было."
    )


def test_order_rejected_text():
    order = {
        "id": "o1", "number": 17, "status": "Rejected",
        "items": [{"dishId": TARTARE["id"], "dishName": TARTARE["name"], "quantity": 4}],
        "shortages": [{"productId": "p-salmon", "productName": "Лосось охлаждённый", "unit": "Kg",
                       "required": 0.44, "available": 0.26}],
    }
    assert texts.order_result_text(order) == (
        "Заказ №17 отклонён, не хватает продуктов:\nЛосось охлаждённый: нужно 0,44 кг, есть 0,26 кг"
    )


def test_journal_takes_latest_15_newest_first():
    movements = [
        {"id": str(i), "at": f"2026-10-09T{i:02d}:00:00+00:00", "type": "Supply", "productName": "Сливки 33%",
         "unit": "L", "quantity": 1.5, "note": None}
        for i in range(20)
    ]
    movements[19]["type"] = "WriteOff"
    movements[19]["note"] = "просрочка"
    lines = texts.journal_text(movements, TZ).split("\n")
    assert lines[0] == "Последние операции:"
    assert len(lines) == 16
    assert lines[1] == "09.10 22:00 Списание: Сливки 33% −1,5 л (просрочка)"
    assert lines[2] == "09.10 21:00 Поставка: Сливки 33% +1,5 л"


def test_stock_texts():
    item = {"productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "fresh": 2.6, "expired": 0.9,
            "freshValue": 6240.0, "batches": 2, "nearestExpiry": "2026-10-10T06:00:00+00:00"}
    assert texts.stock_item_text(item, TZ) == (
        "Сливки 33%\nГодный остаток: 2,6 л на 6 240 ₽\nПросрочено: 0,9 л\nПартий: 2\nБлижайший срок: 10.10 09:00"
    )
    assert texts.stock_summary_text([item]) == (
        "Остатки:\nСливки 33% — 2,6 л, просрочено 0,9 л\n\nГодных продуктов на 6 240 ₽"
    )


def test_hot_offers_404_text():
    assert texts.hot_offers_text(None) == "Горячие предложения скоро появятся."


def test_stock_summary_lists_products_without_stock_separately():
    # API отдаёт в /api/stock все продукты справочника, в том числе без остатка
    def row(name, fresh, expired=0, value=0):
        return {"productId": name, "productName": name, "unit": "Kg", "fresh": fresh, "expired": expired,
                "freshValue": value, "batches": 1 if fresh or expired else 0, "nearestExpiry": None}

    items = [row("Лосось", 1.2, value=2280), row("Икра", 0), row("Тунец", 0, expired=0.3), row("Авокадо", 0)]
    assert texts.stock_summary_text(items) == (
        "Остатки:\nЛосось — 1,2 кг\nТунец — 0 кг, просрочено 0,3 кг\n\n"
        "Годных продуктов на 2 280 ₽\nНет на складе: Авокадо, Икра"
    )
    assert texts.stock_summary_text([row("Икра", 0)]) == "Склад пуст."



def test_journal_new_types_and_actor():
    movements = [
        {"id": 3, "at": "2026-10-11T10:02:00+00:00", "type": "PrepMade", "productName": "Основа том ям", "unit": "L",
         "quantity": 2, "note": "Заготовка", "actor": "Повар"},
        {"id": 2, "at": "2026-10-11T10:01:00+00:00", "type": "PrepUse", "productName": "Лосось", "unit": "Kg",
         "quantity": 0.6, "note": None, "actor": "Повар"},
    ]
    assert texts.journal_text(movements, TZ).split("\n")[1:] == [
        "11.10 13:02 Заготовка: Основа том ям +2 л (Заготовка), Повар",
        "11.10 13:01 На заготовку: Лосось −0,6 кг, Повар",
    ]


def test_hot_offer_with_discount_and_pin():
    # reason скидки — как его прислал развёрнутый API: процент и цена уже внутри.
    reason = "При обычном спросе до конца срока уйдёт около 0,85 кг из 1,98 кг. Скидка 10% — 648 ₽ вместо 720 ₽."
    offer = {**HOT_OFFER, "pinned": True, "discount": {"percent": 10, "price": 648, "floorPrice": 40, "reason": reason}}
    text = texts.hot_offers_text([offer])
    assert text.startswith("Горячие предложения:\n\nТом ям (закреплено) — продать 8 порций")
    assert text.endswith("\n" + reason)
    assert text.count("Скидка 10%") == 1

    bare = {**HOT_OFFER, "discount": {"percent": 20, "price": 552, "floorPrice": 300, "reason": ""}}
    assert texts.hot_offers_text([bare]).endswith("Скидка 20%: 552 ₽ вместо 690 ₽")


def test_purchase_text_with_supplier_and_sum():
    item = {"productId": "p", "productName": "Лосось", "unit": "Kg", "quantity": 5.2, "fresh": 2.1, "perDay": 2.4,
            "hoursCovered": 20.0, "reason": "Хватит на 20 ч.", "supplier": "ООО Рыба", "sum": 9880}
    assert texts.purchase_text([item]) == (
        "Что заказать:\n\nЛосось — заказать 5,2 кг на 9 880 ₽\nПоставщик: ООО Рыба\nХватит на 20 ч."
    )


def test_stop_cost_text():
    report = {"days": 7, "total": 4500, "dishes": [
        {"dishId": "d1", "dishName": "Борщ", "stopHours": 12.25, "portionsPerHour": 0.5, "lostPortions": 6.1, "lostRevenue": 3000},
        {"dishId": "d2", "dishName": "Том ям", "stopHours": 3, "portionsPerHour": 0.5, "lostPortions": 1.5, "lostRevenue": 1500},
    ]}
    assert texts.stop_cost_text(report) == (
        "Недополучено из-за стоп-листа за 7 дн.: 4 500 ₽\n\n"
        "1. Борщ — 12,3 ч в стопе, не продано 6,1 порц., 3 000 ₽\n"
        "2. Том ям — 3 ч в стопе, не продано 1,5 порц., 1 500 ₽"
    )
    assert texts.stop_cost_text({"days": 7, "total": 0, "dishes": []}) == "За 7 дн. блюда в стоп-лист не попадали."


def test_supply_result_with_supplier_and_total():
    supply = {"number": "П-1", "supplier": "ООО Рыба", "total": 4560,
              "items": [{"productName": "Лосось", "unit": "Kg", "quantity": 2.4, "expiresAt": "2026-10-12T09:00:00+00:00"}]}
    assert texts.supply_result_text(supply, TZ) == (
        "Поставка П-1 принята, поставщик ООО Рыба, на 4 560 ₽. Партии:\nЛосось — 2,4 кг, годен до 12.10 12:00"
    )


def test_prep_product_marked_in_list():
    prep = {"name": "Основа том ям", "unit": "L", "shelfLifeHours": 48, "cost": 210, "prepRecipe": [{"productId": "p", "quantity": 1}]}
    assert texts.product_line(prep) == "Основа том ям — л, хранение 48 ч, 210 ₽ за л, заготовка"
