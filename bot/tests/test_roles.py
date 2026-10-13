"""Роли, отказ 403 и действия новой версии API: закрытие стола, скидка, заготовки, ручной стоп."""

import pytest

from ostatok_bot.api import Forbidden, NotFound, ValidationFailed
from ostatok_bot.texts import FORBIDDEN

from .conftest import CARBONARA, CREAM, DISHES, ME, PRODUCTS, SALMON, TARTARE, TZ, batch, stop_item

pytestmark = pytest.mark.asyncio


@pytest.fixture
def make(h):
    def login(role):
        h.login("tok", role=role)
        h.api.returns["list_products"] = PRODUCTS
        h.api.returns["list_dishes"] = DISHES
        h.api.returns["get_stop_list"] = []
        return h
    return login


# Кто что может: бот останавливает действие сразу, не дожидаясь 403


@pytest.mark.parametrize(
    "role, command, expected",
    [
        ("Chef", "/order", "Нет доступа: оформлять заказы можно только официанту."),
        ("Storekeeper", "/close", "Нет доступа: закрывать столы можно только официанту."),
        ("Waiter", "/supply", "Нет доступа: принимать поставки можно только управляющему, шефу и кладовщику."),
        ("Cook", "/supply", "Нет доступа: принимать поставки можно только управляющему, шефу и кладовщику."),
        ("Storekeeper", "/preps", "Нет доступа: готовить заготовки можно только управляющему, шефу и повару."),
        ("Waiter", "/buy", "Нет доступа: смотреть закупки можно только управляющему, шефу и кладовщику."),
        ("Cook", "/losses", "Нет доступа: смотреть аналитику можно только управляющему и шефу."),
        ("Storekeeper", "/stopcost", "Нет доступа: смотреть аналитику можно только управляющему и шефу."),
    ],
)
async def test_denied_by_role_without_calling_api(make, role, command, expected):
    h = make(role)
    assert await h.send(command) == [expected]
    assert h.api.calls == []


async def test_views_are_open_to_every_role(make):
    for role in ("Waiter", "Cook", "Storekeeper", "Manager"):
        h = make(role)
        h.api.calls.clear()
        h.api.returns["get_batches"] = []
        assert await h.send("/stoplist") == ["Все блюда доступны."]
        assert await h.send("/expiring") == ["Нет партий, которые истекают в ближайшие сутки."]


async def test_write_off_button_only_for_stock_roles(make):
    h = make("Cook")
    h.api.returns["get_batches"] = [batch("b1", "Expired", -3)]
    await h.send("/expiring")
    assert h.buttons() == []
    assert await h.click("wo:b1") == ["Нет доступа: списывать партии можно только управляющему, шефу и кладовщику."]
    assert h.api.called("write_off_batch") == []

    h = make("Storekeeper")
    await h.send("/expiring")
    assert h.buttons() == ["Списать"]


async def test_catalog_buttons_only_for_manager_and_chef(make):
    h = make("Cook")
    await h.send("/products")
    assert h.last_markup is None
    assert await h.click("pr:add") == ["Нет доступа: менять справочник продуктов можно только управляющему и шефу."]
    await h.send("/dishes")
    assert h.buttons() == ["Карбонара — 540 ₽", "Тартар из лосося — 690 ₽"]
    await h.press("Карбонара — 540 ₽")
    assert h.buttons() == ["Снять с продажи", "К списку"]

    h = make("Waiter")
    await h.send("/dishes")
    await h.press("Карбонара — 540 ₽")
    assert h.buttons() == ["К списку"]
    assert await h.click(f"ds:e:{CARBONARA['id']}") == ["Нет доступа: менять меню можно только управляющему и шефу."]


async def test_server_403_is_explained_and_dialog_closed(make):
    # Например, роль сменили, а токен ещё действует для чтения, или правила на сервере строже, чем думал бот.
    h = make("Manager")
    h.api.returns["get_purchase_suggestions"] = Forbidden(403)
    assert await h.send("/buy") == [FORBIDDEN]


# Официант: заказ со скидкой и закрытие стола


HOT = [{
    "dishId": TARTARE["id"], "dishName": TARTARE["name"], "photoUrl": None, "price": 690, "score": 1.0,
    "portions": 2, "availablePortions": 4, "valueAtRisk": 500.0, "ingredients": [], "reason": "Лосось истекает.",
    "discount": {"percent": 20, "price": 552, "floorPrice": 300, "reason": "Цена выше себестоимости."}, "pinned": False,
}]


async def test_order_with_hot_offer_discount(make):
    h = make("Waiter")
    h.api.returns["get_me"] = {**ME, "places": []}
    h.api.returns["get_hot_offers"] = HOT
    await h.send("/order")
    assert "Тартар из лосося — скидка 20%, 552 ₽" in h.buttons()
    await h.press("Тартар из лосося — скидка 20%, 552 ₽")
    assert await h.send("3") == [
        "Продать «Тартар из лосося» со скидкой 20%: 552 ₽ вместо 690 ₽? "
        "Сервер применит скидку, только если горячее предложение ещё действует."
    ]
    assert await h.press("Со скидкой") == ["В заказе:\nТартар из лосося × 3 — 2 070 ₽, просим скидку 20%"]
    await h.press("Добавить ещё блюдо")
    await h.press("Тартар из лосося — скидка 20%, 552 ₽")
    assert await h.send("1") == ["В заказе:\nТартар из лосося × 4 — 2 760 ₽, просим скидку 20%"]  # второй раз не спрашивает
    assert await h.press("Готово") == [
        "Заказ:\nТартар из лосося × 4 — 2 760 ₽, просим скидку 20%\nИтого без скидки: 2 760 ₽\n\nСоздать заказ?"
    ]
    h.api.returns["create_order"] = {
        "id": "o1", "number": 7, "status": "Accepted", "place": "", "total": 2484, "closedAt": None,
        "items": [{"dishId": TARTARE["id"], "dishName": TARTARE["name"], "quantity": 4,
                   "discountPercent": 20, "discountQuantity": 2, "price": 690}],
        "shortages": [], "writeOffs": [],
    }
    assert await h.press("Создать заказ") == ["Отправляю заказ…", 
        "Заказ №7 принят.\nТартар из лосося × 4, скидка 20% на 2 порц.\nСумма: 2 484 ₽"
    ]
    assert h.api.called("create_order")[0][:3] == ("tok", [{"dishId": TARTARE["id"], "quantity": 4, "discount": True}], None)


async def test_order_without_discount_when_declined(make):
    h = make("Waiter")
    h.api.returns["get_me"] = {**ME, "places": []}
    h.api.returns["get_hot_offers"] = HOT
    await h.send("/order")
    await h.press("Тартар из лосося — скидка 20%, 552 ₽")
    await h.send("1")
    assert await h.press("Без скидки") == ["В заказе:\nТартар из лосося × 1 — 690 ₽"]


async def test_manually_stopped_dish_cannot_be_ordered(make):
    h = make("Waiter")
    h.api.returns["get_stop_list"] = [stop_item(TARTARE, status="Stop", reason="Manual")]
    await h.send("/order")
    assert await h.press("Тартар из лосося — стоп") == [
        "Блюдо в стопе: снято с продажи вручную. Выбрать его нельзя."
    ]


def summary(*tables):
    return {"orders": 5, "rejected": 1, "revenue": 1920, "tables": [
        {"place": place, "total": total, "last": "2026-10-09T11:40:00+00:00"} for place, total in tables
    ]}


async def test_close_table(make):
    h = make("Waiter")
    h.api.returns["get_orders_summary"] = summary(("Стол 7", 1620), ("Бар", 300))
    assert await h.send("/close") == ["Какой стол закрыть?"]
    assert h.buttons() == ["Стол 7 — 1 620 ₽", "Бар — 300 ₽", "Отмена"]
    assert await h.press("Стол 7 — 1 620 ₽") == ["Закрыть Стол 7? Открыто заказов на 1 620 ₽."]
    h.api.returns["close_table"] = {"place": "Стол 7", "orders": 2, "total": 1620}
    assert await h.press("Закрыть стол") == ["Стол 7 закрыт: 2 заказа на 1 620 ₽."]
    assert h.api.called("close_table") == [("tok", "Стол 7")]
    # Столы считает сервер; бот спрашивает итоги с начала дня по часовому поясу ресторана.
    name, args, kwargs = h.api.calls[0]
    assert (name, args) == ("get_orders_summary", ("tok",))
    since = kwargs["since"].astimezone(TZ)
    assert (since.hour, since.minute, since.second) == (0, 0, 0)


async def test_close_table_404_and_nothing_open(make):
    h = make("Waiter")
    h.api.returns["get_orders_summary"] = summary(("Бар", 300))
    await h.send("/close")
    await h.press("Бар — 300 ₽")
    h.api.returns["close_table"] = NotFound(404)
    assert await h.press("Закрыть стол") == ["У места «Бар» нет открытых заказов: возможно, его уже закрыли."]

    h.api.returns["get_orders_summary"] = summary()
    assert await h.send("/close") == ["Открытых столов нет."]


# Кухня: заготовки и ручной стоп


PREPS = [{
    "productId": "p-tomyum", "productName": "Основа том ям", "unit": "L", "shelfLifeHours": 48, "fresh": 1.2,
    "perDay": 2.4, "hoursCovered": 12, "suggested": 2, "reason": "Запаса хватит на 12 ч.", "maxQuantity": 3.5,
    "ingredients": [],
}]


async def open_prep(h):
    h.api.returns["get_preps"] = PREPS
    replies = await h.send("/preps")
    assert replies == [
        "Заготовки:\n\nОснова том ям — есть 1,2 л\nПриготовить: 2 л. Запаса хватит на 12 ч.\n"
        "Из сырья можно сделать до 3,5 л"
    ]
    assert await h.press("Приготовить: Основа том ям") == [
        "Основа том ям. Сколько приготовить, л? Из сырья хватит на 3,5 л."
    ]


async def test_prep_made(make):
    h = make("Cook")
    await open_prep(h)
    assert await h.send("1001") == ["За раз — не больше 1000. Сколько приготовить, л?"]
    assert await h.press("По плану: 2 л") == ["Приготовить 2 л: Основа том ям? Сырьё спишется со склада."]
    h.api.returns["make_prep"] = {"batchId": "b9", "supplyNumber": "З-0007", "quantity": 2,
                                  "expiresAt": "2026-10-13T09:00:00+00:00", "shortages": []}
    assert await h.press("Приготовить") == ["Готово: Основа том ям, 2 л. Партия З-0007, годна до 13.10 12:00."]
    [call] = h.api.called("make_prep")
    # Четвёртый аргумент — идентификатор попытки (GUID): повтор запроса сервер не задвоит.
    assert call[:3] == ("tok", "p-tomyum", 2) and len(call[3]) == 36


async def test_prep_409_shortage_and_400(make):
    h = make("Chef")
    await open_prep(h)
    await h.send("3")
    h.api.returns["make_prep"] = {"batchId": None, "supplyNumber": "", "quantity": 0, "expiresAt": None, "shortages": [
        {"productId": "p-salmon", "productName": "Лосось охлаждённый", "unit": "Kg", "required": 0.6, "available": 0.2}
    ]}
    assert await h.press("Приготовить") == [
        "Не хватает сырья на «Основа том ям», ничего не списано:\nЛосось охлаждённый: нужно 0,6 кг, есть 0,2 кг"
    ]
    await open_prep(h)
    await h.send("1")
    h.api.returns["make_prep"] = ValidationFailed(400, {"errors": {"productId": ["Такой заготовки нет."]}})
    assert await h.press("Приготовить") == ["Не приготовлено.\nТакой заготовки нет.\nНачать заново: /preps"]


async def test_stop_and_resume_dish(make):
    h = make("Cook")
    await h.send("/dishes")
    await h.press("Тартар из лосося — 690 ₽")
    assert await h.press("Снять с продажи") == ["Снять «Тартар из лосося» с продажи? Официанты не смогут его заказать."]
    assert await h.press("Снять с продажи") == [
        "«Тартар из лосося» снято с продажи. Вернуть: /dishes → блюдо → «Вернуть в продажу»."
    ]
    assert h.api.called("stop_dish") == [("tok", TARTARE["id"])]

    h.api.returns["list_dishes"] = [{**TARTARE, "manuallyStopped": True}, CARBONARA]
    await h.send("/dishes")
    assert "Тартар из лосося — снято с продажи" in h.buttons()
    text = await h.press("Тартар из лосося — снято с продажи")
    assert text[0].startswith("Тартар из лосося\nСнято с продажи вручную\nРаздел меню: Холодные закуски\nЦена: 690 ₽")
    await h.press("Вернуть в продажу")
    h.api.returns["resume_dish"] = NotFound(404)
    assert await h.press("Вернуть в продажу") == ["Блюдо «Тартар из лосося» не найдено, возможно его удалили."]


# Справочник: поля, которых бот не меняет, не стираются


async def test_editing_a_prep_product_keeps_its_recipe_and_skips_cost(make):
    h = make("Chef")
    prep = {"id": "p-tomyum", "name": "Основа том ям", "unit": "L", "shelfLifeHours": 48, "cost": 210,
            "prepRecipe": [{"productId": SALMON["id"], "quantity": 0.3}]}
    h.api.returns["list_products"] = PRODUCTS + [prep]
    await h.send("/products")
    await h.press("Изменить")
    await h.press("Основа том ям (л)")
    await h.press("Оставить")
    await h.press("Оставить")
    replies = await h.send("72")
    assert replies[0].endswith("Сохранить изменения?")     # цену заготовки не спрашивает
    h.api.returns["update_product"] = {**prep, "shelfLifeHours": 72}
    await h.press("Сохранить")
    assert h.api.called("update_product") == [
        ("tok", "p-tomyum", "Основа том ям", "L", 72, 210, [{"productId": SALMON["id"], "quantity": 0.3}], None)
    ]


async def test_supply_rejects_system_numbers(make):
    h = make("Storekeeper")
    await h.send("/supply")
    assert await h.send("З-0001") == ["Номера на «З-», «Т-» и «Г-» заняты системой. Введите другой номер."]


async def test_other_restaurant_data_comes_only_from_token(make):
    # Ресторан бот нигде не передаёт: его определяет сервер по токену.
    h = make("Storekeeper")
    h.api.returns["get_stock"] = [{"productId": CREAM["id"], "productName": "Сливки 33%", "unit": "L", "fresh": 1,
                                   "expired": 0, "freshValue": 380, "batches": 1, "nearestExpiry": None}]
    await h.send("/stock")
    assert h.api.calls == [("get_stock", ("tok",), {})]
