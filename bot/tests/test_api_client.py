"""ApiClient на подставном HTTP-транспорте: пути, заголовки, тела и разбор кодов ответа."""

import json

import httpx
import pytest

from ostatok_bot.api import (
    ApiClient,
    ApiError,
    ApiUnavailable,
    Conflict,
    Forbidden,
    NotFound,
    TooManyRequests,
    Unauthorized,
    ValidationFailed,
    restaurant_of,
)


def client(handler) -> tuple[ApiClient, list[httpx.Request]]:
    seen: list[httpx.Request] = []

    def wrapped(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return handler(request)

    http = httpx.AsyncClient(base_url="http://api", transport=httpx.MockTransport(wrapped))
    return ApiClient("http://api", http=http, retry_delays=(0, 0)), seen


def body(request: httpx.Request):
    return json.loads(request.content) if request.content else None


async def test_login_ok_has_no_auth_header():
    api, seen = client(lambda r: httpx.Response(200, json={"token": "T", "expiresAt": "2026-10-10T06:00:00+00:00"}))
    result = await api.login("admin", "pwd")
    assert result.token == "T"
    assert result.expires_at.isoformat() == "2026-10-10T06:00:00+00:00"
    assert seen[0].url.path == "/api/auth/login"
    assert body(seen[0]) == {"login": "admin", "password": "pwd"}
    assert "authorization" not in seen[0].headers


@pytest.mark.parametrize("status, exc", [(401, Unauthorized), (429, TooManyRequests)])
async def test_login_errors(status, exc):
    api, _ = client(lambda r: httpx.Response(status))
    with pytest.raises(exc):
        await api.login("a", "b")


async def test_bearer_token_is_sent():
    api, seen = client(lambda r: httpx.Response(200, json=[]))
    await api.get_stop_list("TOKEN")
    assert seen[0].headers["authorization"] == "Bearer TOKEN"
    assert seen[0].url.path == "/api/stop-list"


@pytest.mark.parametrize(
    "status, exc",
    [(400, ValidationFailed), (401, Unauthorized), (404, NotFound), (409, Conflict),
     (500, ApiUnavailable), (503, ApiUnavailable), (418, ApiError)],
)
async def test_status_mapping(status, exc):
    api, _ = client(lambda r: httpx.Response(status, json={"errors": {"name": ["Ошибка"]}}))
    with pytest.raises(exc):
        await api.list_products("t")


async def test_network_error_is_api_unavailable():
    def fail(request):
        raise httpx.ConnectError("refused")

    api, _ = client(fail)
    with pytest.raises(ApiUnavailable):
        await api.get_batches("t")


async def test_validation_messages():
    payload = {"errors": {"name": ["Название обязательно"], "cost": ["Цена не может быть отрицательной", "Ещё"]}}
    api, _ = client(lambda r: httpx.Response(400, json=payload))
    with pytest.raises(ValidationFailed) as info:
        await api.create_product("t", "", "Kg", 1, -1)
    assert info.value.messages() == ["Название обязательно", "Цена не может быть отрицательной", "Ещё"]


async def test_write_off_204():
    api, seen = client(lambda r: httpx.Response(204))
    assert await api.write_off_batch("t", "b1") is None
    assert seen[0].method == "POST"
    assert seen[0].url.path == "/api/stock/batches/b1/write-off"


@pytest.mark.parametrize("status, exc", [(404, NotFound), (409, Conflict)])
async def test_write_off_errors(status, exc):
    api, _ = client(lambda r: httpx.Response(status))
    with pytest.raises(exc):
        await api.write_off_batch("t", "b1")


async def test_create_supply_body_and_201():
    api, seen = client(lambda r: httpx.Response(201, json={"id": "s1", "number": "П-1", "items": []}))
    result = await api.create_supply("t", "П-1", [
        {"productId": "p1", "quantity": 2.0, "shelfLifeHours": None},
        {"productId": "p2", "quantity": 0.5, "shelfLifeHours": 24},
    ])
    assert result["number"] == "П-1"
    assert body(seen[0]) == {"number": "П-1", "items": [
        {"productId": "p1", "quantity": 2},
        {"productId": "p2", "quantity": 0.5, "shelfLifeHours": 24},
    ]}


async def test_create_supply_409():
    api, _ = client(lambda r: httpx.Response(409))
    with pytest.raises(Conflict):
        await api.create_supply("t", "П-1", [{"productId": "p1", "quantity": 1}])


async def test_create_order_201_and_409_rejected_return_order():
    accepted = {"id": "o1", "number": 1, "status": "Accepted", "items": [], "shortages": []}
    rejected = {"id": "o2", "number": 2, "status": "Rejected", "items": [], "shortages": [{"productName": "x"}]}
    api, seen = client(lambda r: httpx.Response(201, json=accepted))
    assert (await api.create_order("t", [{"dishId": "d1", "quantity": 2}]))["status"] == "Accepted"
    assert body(seen[0]) == {"items": [{"dishId": "d1", "quantity": 2}]}
    api, _ = client(lambda r: httpx.Response(409, json=rejected))
    assert (await api.create_order("t", [{"dishId": "d1", "quantity": 2}]))["status"] == "Rejected"


async def test_create_order_409_without_order_body_is_conflict():
    api, _ = client(lambda r: httpx.Response(409, json={"title": "Conflict"}))
    with pytest.raises(Conflict):
        await api.create_order("t", [{"dishId": "d1", "quantity": 1}])


async def test_create_order_400():
    api, _ = client(lambda r: httpx.Response(400, json={"errors": {"items": ["Пустой заказ"]}}))
    with pytest.raises(ValidationFailed):
        await api.create_order("t", [])


async def test_product_crud_paths_and_codes():
    responses = {
        ("POST", "/api/products"): httpx.Response(201, json={"id": "p1"}),
        ("PUT", "/api/products/p1"): httpx.Response(200, json={"id": "p1"}),
        ("DELETE", "/api/products/p1"): httpx.Response(204),
    }
    api, seen = client(lambda r: responses[(r.method, r.url.path)])
    assert await api.create_product("t", "Сыр", "Kg", 720, 2400.0) == {"id": "p1"}
    assert body(seen[0]) == {"name": "Сыр", "unit": "Kg", "shelfLifeHours": 720, "cost": 2400}
    assert await api.update_product("t", "p1", "Сыр", "Kg", 720, 2400.5) == {"id": "p1"}
    assert await api.delete_product("t", "p1") is None


@pytest.mark.parametrize(
    "call, status, exc",
    [
        ("create", 409, Conflict), ("update", 404, NotFound), ("update", 409, Conflict),
        ("delete", 404, NotFound), ("delete", 409, Conflict),
    ],
)
async def test_product_errors(call, status, exc):
    api, _ = client(lambda r: httpx.Response(status))
    calls = {
        "create": lambda: api.create_product("t", "a", "L", 1, 1),
        "update": lambda: api.update_product("t", "p1", "a", "L", 1, 1),
        "delete": lambda: api.delete_product("t", "p1"),
    }
    with pytest.raises(exc):
        await calls[call]()


async def test_dish_crud_paths_codes_and_whole_recipe():
    responses = {
        ("POST", "/api/dishes"): httpx.Response(201, json={"id": "d1"}),
        ("PUT", "/api/dishes/d1"): httpx.Response(200, json={"id": "d1"}),
        ("DELETE", "/api/dishes/d1"): httpx.Response(204),
    }
    api, seen = client(lambda r: responses[(r.method, r.url.path)])
    recipe = [{"productId": "p1", "quantity": 0.12}, {"productId": "p2", "quantity": 1.0}]
    await api.create_dish("t", "Тартар", 690.0, recipe)
    await api.update_dish("t", "d1", "Тартар", 700, recipe)
    assert body(seen[1]) == {"name": "Тартар", "price": 700,
                             "recipe": [{"productId": "p1", "quantity": 0.12}, {"productId": "p2", "quantity": 1}]}
    assert await api.delete_dish("t", "d1") is None


@pytest.mark.parametrize(
    "call, status, exc",
    [("create", 409, Conflict), ("create", 400, ValidationFailed), ("update", 404, NotFound),
     ("delete", 404, NotFound), ("delete", 409, Conflict)],
)
async def test_dish_errors(call, status, exc):
    api, _ = client(lambda r: httpx.Response(status))
    calls = {
        "create": lambda: api.create_dish("t", "a", 1, []),
        "update": lambda: api.update_dish("t", "d1", "a", 1, []),
        "delete": lambda: api.delete_dish("t", "d1"),
    }
    with pytest.raises(exc):
        await calls[call]()


async def test_dish_photo_url_is_sent_only_when_set():
    api, seen = client(lambda r: httpx.Response(200, json={"id": "d1"}))
    recipe = [{"productId": "p1", "quantity": 1}]
    await api.update_dish("t", "d1", "Том ям", 690, recipe, "/dishes/tom-yum.jpg")
    await api.update_dish("t", "d1", "Том ям", 690, recipe)
    assert body(seen[0])["photoUrl"] == "/dishes/tom-yum.jpg"
    assert "photoUrl" not in body(seen[1])


async def test_purchase_suggestions_path():
    api, seen = client(lambda r: httpx.Response(200, json=[]))
    assert await api.get_purchase_suggestions("t") == []
    assert seen[0].url.path == "/api/purchase-suggestions"


async def test_write_off_analytics_days_param():
    report = {"days": 7, "total": 0, "byDay": [], "byProduct": [], "byReason": []}
    api, seen = client(lambda r: httpx.Response(200, json=report))
    assert await api.get_write_off_analytics("t", days=7) == report
    assert seen[0].url.path == "/api/analytics/write-offs"
    assert seen[0].url.params["days"] == "7"


async def test_hot_offers_404_returns_none():
    api, _ = client(lambda r: httpx.Response(404))
    assert await api.get_hot_offers("t") is None
    api, _ = client(lambda r: httpx.Response(200, json=[]))
    assert await api.get_hot_offers("t") == []


async def test_login_401_and_429_extensions_from_problem_details():
    # так их отдаёт Results.Problem(..., extensions) на бэкенде
    wrong = {"type": "https://tools.ietf.org/html/rfc9110#section-15.5.2", "title": "Unauthorized", "status": 401,
             "detail": "Неверный логин или пароль.", "attemptsLeft": 3}
    api, _ = client(lambda r: httpx.Response(401, json=wrong))
    with pytest.raises(Unauthorized) as info:
        await api.login("demo", "нет")
    assert info.value.attempts_left == 3

    locked = {"title": "Too Many Requests", "status": 429,
              "detail": "Слишком много неверных попыток. Вход временно закрыт.", "retryAfterMinutes": 10}
    api, _ = client(lambda r: httpx.Response(429, json=locked))
    with pytest.raises(TooManyRequests) as info:
        await api.login("demo", "нет")
    assert info.value.retry_after_minutes == 10

    api, _ = client(lambda r: httpx.Response(429))
    with pytest.raises(TooManyRequests) as info:
        await api.login("demo", "нет")
    assert info.value.retry_after_minutes is None


async def test_get_me():
    me = {"login": "demo", "title": "Шеф-повар", "shift": {"start": "12:00", "end": "24:00"}, "places": ["Стол 1", "Бар"]}
    api, seen = client(lambda r: httpx.Response(200, json=me))
    assert await api.get_me("t") == me
    assert seen[0].url.path == "/api/me"
    assert seen[0].headers["authorization"] == "Bearer t"


async def test_create_order_place_is_sent_only_when_chosen():
    accepted = {"id": "o1", "number": 1, "status": "Accepted", "place": "Стол 7", "total": 690,
                "items": [], "shortages": [], "writeOffs": []}
    api, seen = client(lambda r: httpx.Response(201, json=accepted))
    await api.create_order("t", [{"dishId": "d1", "quantity": 1}], "Стол 7")
    await api.create_order("t", [{"dishId": "d1", "quantity": 1}])
    assert body(seen[0]) == {"items": [{"dishId": "d1", "quantity": 1}], "place": "Стол 7"}
    assert body(seen[1]) == {"items": [{"dishId": "d1", "quantity": 1}]}


async def test_movements_take_param():
    api, seen = client(lambda r: httpx.Response(200, json=[]))
    await api.get_movements("t", take=15)
    assert seen[0].url.path == "/api/movements"
    assert seen[0].url.params["take"] == "15"


async def test_403_is_forbidden():
    api, _ = client(lambda r: httpx.Response(403))
    with pytest.raises(Forbidden):
        await api.create_order("t", [{"dishId": "d1", "quantity": 1}])


def test_restaurant_of_reads_token_claim():
    from .conftest import RESTAURANT, jwt
    assert restaurant_of(jwt()) == RESTAURANT
    assert restaurant_of("not-a-jwt") is None
    assert restaurant_of("a.###.c") is None


async def test_write_off_reason_body_only_for_fresh_batch():
    api, seen = client(lambda r: httpx.Response(204))
    await api.write_off_batch("t", "b1")
    await api.write_off_batch("t", "b1", "Spoiled")
    assert seen[0].content == b""
    assert body(seen[1]) == {"reason": "Spoiled"}


async def test_order_discount_flag_and_close_table():
    accepted = {"id": "o1", "number": 1, "status": "Accepted", "place": "", "total": 1, "items": [],
                "shortages": [], "writeOffs": [], "closedAt": None}
    api, seen = client(lambda r: httpx.Response(201, json=accepted))
    await api.create_order("t", [{"dishId": "d1", "quantity": 2, "discount": True}, {"dishId": "d2", "quantity": 1}])
    assert body(seen[0]) == {"items": [{"dishId": "d1", "quantity": 2, "discount": True}, {"dishId": "d2", "quantity": 1}]}

    api, seen = client(lambda r: httpx.Response(200, json={"place": "Стол 7", "orders": 2, "total": 1620}))
    assert await api.close_table("t", "Стол 7") == {"place": "Стол 7", "orders": 2, "total": 1620}
    assert (seen[0].method, seen[0].url.path, body(seen[0])) == ("POST", "/api/orders/close", {"place": "Стол 7"})

    api, _ = client(lambda r: httpx.Response(404))
    with pytest.raises(NotFound):
        await api.close_table("t", "Бар")


async def test_make_prep_201_409_400():
    made = {"batchId": "b1", "supplyNumber": "З-0001", "quantity": 2, "expiresAt": "2026-10-13T09:00:00+00:00", "shortages": []}
    api, seen = client(lambda r: httpx.Response(201, json=made))
    assert await api.make_prep("t", "p1", 2.0) == made
    assert body(seen[0]) == {"productId": "p1", "quantity": 2}

    short = {**made, "shortages": [{"productId": "p2", "productName": "Лосось", "unit": "Kg", "required": 1, "available": 0}]}
    api, _ = client(lambda r: httpx.Response(409, json=short))
    assert (await api.make_prep("t", "p1", 2))["shortages"][0]["productName"] == "Лосось"

    api, _ = client(lambda r: httpx.Response(400, json={"errors": {"productId": ["Такой заготовки нет."]}}))
    with pytest.raises(ValidationFailed):
        await api.make_prep("t", "p1", 2)


async def test_stop_resume_and_analytics_paths():
    api, seen = client(lambda r: httpx.Response(204) if r.method == "POST" else httpx.Response(200, json={}))
    await api.stop_dish("t", "d1")
    await api.resume_dish("t", "d1")
    await api.get_stop_cost("t", days=7)
    await api.get_alerts("t", take=5)
    await api.mark_alerts_read("t")
    assert [(r.method, r.url.path) for r in seen] == [
        ("POST", "/api/dishes/d1/stop"), ("POST", "/api/dishes/d1/resume"),
        ("GET", "/api/analytics/stop-cost"), ("GET", "/api/alerts"), ("POST", "/api/alerts/read"),
    ]
    assert seen[2].url.params["days"] == "7"


async def test_update_keeps_prep_recipe_and_category():
    api, seen = client(lambda r: httpx.Response(200, json={}))
    await api.update_product("t", "p1", "Основа", "L", 48, 210, [{"productId": "p2", "quantity": 0.3}])
    await api.update_product("t", "p1", "Сливки", "L", 72, 380)
    await api.update_dish("t", "d1", "Тартар", 690, [{"productId": "p2", "quantity": 0.1}], "/dishes/t.jpg", "Холодное")
    assert body(seen[0])["prepRecipe"] == [{"productId": "p2", "quantity": 0.3}]
    assert "prepRecipe" not in body(seen[1])
    assert body(seen[2])["category"] == "Холодное"


async def test_supply_supplier():
    api, seen = client(lambda r: httpx.Response(201, json={"id": "s1", "number": "П-1", "items": []}))
    await api.create_supply("t", "П-1", [{"productId": "p1", "quantity": 1}], "ООО Рыба")
    await api.create_supply("t", "П-2", [{"productId": "p1", "quantity": 1}])
    assert body(seen[0])["supplier"] == "ООО Рыба"
    assert "supplier" not in body(seen[1])
