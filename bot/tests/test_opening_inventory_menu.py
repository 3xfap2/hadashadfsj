"""Срок после вскрытия, инвентаризация и меню для гостя."""

import httpx
import pytest

from ostatok_bot.api import ApiClient, ApiUnavailable, Conflict, ValidationFailed

from .conftest import CREAM, EGG, ME, PRODUCTS, RESTAURANT, SALMON, batch


def openable(id_="b1", hours=30.0, remaining=2.0, opened_at=None, status="Fresh", life=24, product=CREAM):
    return {**batch(id_, status, hours, product=product, remaining=remaining),
            "expiresAt": "2026-10-10T18:00:00+00:00", "openedAt": opened_at, "openedShelfLifeHours": life}


@pytest.fixture
def keeper(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["list_products"] = PRODUCTS
    return h


# Клиент API


def client(handler):
    seen = []

    def wrapped(request):
        seen.append(request)
        return handler(request)

    http = httpx.AsyncClient(base_url="http://api", transport=httpx.MockTransport(wrapped))
    return ApiClient("http://api", http=http, retry_delays=(0, 0)), seen


async def test_api_open_inventory_and_public_menu():
    api, seen = client(lambda r: httpx.Response(200, json={"id": "x", "expiresAt": "2026-10-10T12:00:00Z"}))
    await api.open_batch("t", "b1", 1.5)
    await api.open_batch("t", "b2")
    await api.inventory("t", [{"productId": "p1", "actual": 3.0}])
    await api.get_public_menu("r1")
    assert [(r.method, r.url.path, r.content) for r in seen] == [
        ("POST", "/api/stock/batches/b1/open", b'{"quantity":1.5}'),
        ("POST", "/api/stock/batches/b2/open", b"{}"),
        ("POST", "/api/stock/inventory", b'{"items":[{"productId":"p1","actual":3}]}'),
        ("GET", "/api/public/menu/r1", b""),
    ]
    assert "authorization" not in seen[3].headers   # меню гостя открывается без входа


# Вскрытие


async def test_open_button_only_where_it_makes_sense(keeper):
    keeper.api.returns["get_batches"] = [
        openable("b1", hours=10),                                  # можно вскрыть
        openable("b2", hours=8, opened_at="2026-10-09T08:00:00Z"),  # уже вскрыта
        openable("b3", hours=6, life=None),                         # вскрывать незачем
        openable("b4", hours=-2, status="Expired"),                 # просрочена: только списать
    ]
    replies = await keeper.send("/expiring")
    assert "Сливки 33% — 2 л на 342 ₽, осталось 8 ч, поставка П-0413, вскрыта" in replies[0]
    assert keeper.buttons() == ["Списать: Сливки 33%, 2 л", "Вскрыть: Сливки 33%, 2 л"]
    assert keeper.button_data("Вскрыть: Сливки 33%, 2 л") == "op:b1"


async def test_no_buttons_for_roles_without_stock(h):
    h.login("tok", role="Cook")
    h.api.returns["get_batches"] = [openable("b1", hours=10), openable("b4", hours=-2, status="Expired")]
    await h.send("/expiring")
    assert h.buttons() == []


async def test_open_part_of_batch(keeper):
    keeper.api.returns["get_batches"] = [openable("b1", hours=10)]
    await keeper.send("/expiring")
    assert await keeper.press("Вскрыть") == [
        "Вскрыть Сливки 33% (поставка П-0413)? В партии 2 л, после вскрытия хранится 24 ч.\n"
        "Сколько вскрыть? Введите число или нажмите «Всю партию»."
    ]
    assert await keeper.send("3") == ["В партии только 2 л. Введите меньше или нажмите «Всю партию»."]
    keeper.api.returns["open_batch"] = {"id": "b9", "expiresAt": "2026-10-10T09:00:00+00:00"}
    assert await keeper.send("0,5") == [
        "Вскрыто: Сливки 33%, 0,5 л. Годно до 10.10 12:00.\nОстальное (1,5 л) лежит закрытым, годно до 10.10 21:00."
    ]
    assert keeper.api.called("open_batch") == [("tok", "b1", 0.5)]


async def test_open_whole_batch_and_server_refusal(keeper):
    keeper.api.returns["get_batches"] = [openable("b1", hours=10)]
    await keeper.send("/expiring")
    await keeper.press("Вскрыть")
    keeper.api.returns["open_batch"] = {"id": "b1", "expiresAt": "2026-10-10T09:00:00+00:00"}
    assert await keeper.press("Всю партию") == ["Вскрыто: Сливки 33%, 2 л. Годно до 10.10 12:00."]
    assert keeper.api.called("open_batch") == [("tok", "b1", None)]

    await keeper.send("/expiring")
    await keeper.press("Вскрыть")
    keeper.api.returns["open_batch"] = Conflict(409, {"detail": "Эта партия уже вскрыта."})
    assert await keeper.press("Всю партию") == ["Не вскрыто.\nЭта партия уже вскрыта."]

    await keeper.send("/expiring")
    await keeper.press("Вскрыть")
    keeper.api.returns["open_batch"] = ValidationFailed(
        400, {"errors": {"product": ["У продукта не задан срок после вскрытия. Укажите его в справочнике."]}})
    assert await keeper.send("1") == ["Не вскрыто.\nУ продукта не задан срок после вскрытия. Укажите его в справочнике."]


async def test_stale_open_button(keeper):
    keeper.api.returns["get_batches"] = [openable("b1", hours=10)]
    await keeper.send("/expiring")
    keeper.api.returns["get_batches"] = [openable("b1", hours=10, opened_at="2026-10-09T11:00:00Z")]
    assert await keeper.press("Вскрыть") == ["Эту партию вскрыть нельзя: она уже вскрыта или просрочена."]
    keeper.api.returns["get_batches"] = []
    assert await keeper.click("op:b1") == ["Партия уже израсходована или списана."]


async def test_stock_card_lists_batches_with_open_button(keeper):
    keeper.api.returns["get_stock"] = [
        {"productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "fresh": 3, "expired": 0,
         "freshValue": 1140.0, "batches": 2, "nearestExpiry": "2026-10-10T12:00:00+00:00"},
    ]
    keeper.api.returns["get_batches"] = [
        {**openable("b1"), "remaining": 1, "expiresAt": "2026-10-10T12:00:00+00:00", "openedAt": "2026-10-09T10:00:00Z"},
        openable("b2"),
        openable("b5", product=SALMON),   # другой продукт в карточку не попадает
    ]
    assert await keeper.send("/stock сливки") == [
        "Сливки 33%\nГодный остаток: 3 л на 1 140 ₽\nПартий: 2\nБлижайший срок: 10.10 15:00\n"
        "Партии:\n• 1 л, годна до 10.10 15:00, поставка П-0413, вскрыта\n• 2 л, годна до 10.10 21:00, поставка П-0413"
    ]
    assert keeper.buttons() == ["Вскрыть"]
    assert keeper.button_data("Вскрыть") == "op:b2"


# Инвентаризация


INVENTORY_RESULT = {
    "shortage": 76.0, "surplus": 24.0,
    "rows": [
        {"productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "accounted": 2.7, "actual": 2.5,
         "difference": -0.2, "value": -76.0},
        {"productId": "p-egg", "productName": "Яйцо", "unit": "Pcs", "accounted": 30, "actual": 32,
         "difference": 2, "value": 24.0},
    ],
}


async def test_inventory_dialog(keeper):
    keeper.api.returns["get_batches"] = [
        batch("b1", "Expired", -3, CREAM, remaining=0.9), batch("b2", "Fresh", 40, CREAM, remaining=1.8),
        batch("b3", "Fresh", 100, EGG, remaining=30),
    ]
    assert await keeper.send("/inventory") == ["Какой продукт пересчитать?"]
    assert await keeper.press("Сливки 33% (л)") == [
        "Сливки 33%: по учёту 2,7 л, из них просрочено 0,9 л.\nСколько по факту, вместе с просроченным? Введите число."
    ]
    assert (await keeper.send("-1"))[0].startswith("Остаток не может быть отрицательным.")
    assert await keeper.send("2,5") == [
        "Пересчитано:\n1. Сливки 33%: по учёту 2,7 л, по факту 2,5 л (−0,2 л)\n\n"
        "Остатки изменятся под факт. Провести инвентаризацию?"
    ]
    assert keeper.buttons() == ["Провести инвентаризацию", "Ещё продукт", "Отмена"]
    assert await keeper.press("Ещё продукт") == ["Пересчитано продуктов: 1. Какой ещё?"]
    await keeper.press("Яйцо (шт)")
    replies = await keeper.send("32")
    assert "2. Яйцо: по учёту 30 шт, по факту 32 шт (+2 шт)" in replies[0]

    keeper.api.returns["inventory"] = INVENTORY_RESULT
    assert await keeper.press("Провести инвентаризацию") == [
        "Инвентаризация проведена. Расхождения:\n"
        "Сливки 33%: по учёту 2,7 л, по факту 2,5 л, недостача 0,2 л на 76 ₽\n"
        "Яйцо: по учёту 30 шт, по факту 32 шт, излишек 2 шт на 24 ₽\n\n"
        "Недостача: 76 ₽\nИзлишек: 24 ₽\nВ журнале: /journal"
    ]
    assert keeper.api.called("inventory") == [
        ("tok", [{"productId": "p-cream", "actual": 2.5}, {"productId": "p-egg", "actual": 32}])
    ]


async def test_inventory_recount_replaces_and_zero_is_allowed(keeper):
    keeper.api.returns["get_batches"] = []
    await keeper.send("/inventory")
    await keeper.press("Яйцо (шт)")
    await keeper.send("5")
    await keeper.press("Ещё продукт")
    await keeper.press("Яйцо (шт)")
    replies = await keeper.send("0")
    assert replies[0].startswith("Пересчитано:\n1. Яйцо: по учёту 0 шт, по факту 0 шт (совпадает)\n")
    keeper.api.returns["inventory"] = {"shortage": 0, "surplus": 0, "rows": []}
    assert await keeper.press("Провести инвентаризацию") == [
        "Инвентаризация проведена: расхождений нет, по факту всё совпало с учётом."
    ]


async def test_inventory_refused_by_server(keeper):
    keeper.api.returns["get_batches"] = []
    await keeper.send("/inventory")
    await keeper.press("Яйцо (шт)")
    await keeper.send("3")
    keeper.api.returns["inventory"] = ValidationFailed(
        400, {"errors": {"items": ["Яйцо: годных партий нет, излишек записать некуда. Оформите его поставкой."]}})
    assert await keeper.press("Провести инвентаризацию") == [
        "Инвентаризация не проведена, остатки не изменились.\n"
        "Яйцо: годных партий нет, излишек записать некуда. Оформите его поставкой.\nНачать заново: /inventory"
    ]


@pytest.mark.parametrize("role", ["Waiter", "Cook"])
async def test_inventory_is_for_stock_roles(h, role):
    h.login("tok", role=role)
    assert await h.send("/inventory") == [
        "Нет доступа: проводить инвентаризацию можно только управляющему, шефу и кладовщику."
    ]


# Меню для гостя


async def test_menu_link_for_manager(h):
    h.login("tok", role="Manager")
    h.api.returns["get_me"] = {**ME, "restaurantId": "r-42", "restaurant": "Демо-ресторан"}
    h.api.returns["get_public_menu"] = {"restaurant": "Демо-ресторан", "dishes": [
        {"dishId": "d1", "name": "Том ям", "price": 690, "offerPrice": 550, "offerPercent": 20},
        {"dishId": "d2", "name": "Карбонара", "price": 540, "offerPrice": None, "offerPercent": None},
    ]}
    assert await h.send("/menu_link") == [
        "Меню для гостей «Демо-ресторан»:\nhttp://api/menu/r-42\n\n"
        "Сейчас в меню 2 блюда, по предложению дня — 1. Блюда из стоп-листа гости не видят.\n"
        "Ссылку можно отправить гостю или напечатать QR-кодом на столы."
    ]
    assert h.api.called("get_public_menu") == [("r-42",)]


async def test_menu_link_without_restaurant_id_in_me_and_menu_down(h):
    h.login("tok", role="Manager")
    h.api.returns["get_me"] = ME   # старый ответ без restaurantId: берётся ресторан из токена
    h.api.returns["get_public_menu"] = ApiUnavailable(502)
    replies = await h.send("/menu_link")
    assert replies[0].startswith(f"Меню для гостей «Демо-ресторан»:\nhttp://api/menu/{RESTAURANT}\n\n"
                                 "Проверить меню сейчас не удалось")


@pytest.mark.parametrize("role", ["Chef", "Storekeeper", "Waiter"])
async def test_menu_link_is_for_manager_only(h, role):
    h.login("tok", role=role)
    assert await h.send("/menu_link") == ["Нет доступа: получать ссылку на меню для гостей можно только управляющему."]
