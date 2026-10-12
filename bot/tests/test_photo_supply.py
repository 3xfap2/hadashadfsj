"""Поставка по фото, повтор заказа с тем же id, план спасения в /hot и роли без доступа к ценам."""

import json
import uuid

import httpx
import pytest
from aiogram.types import Document, PhotoSize

from ostatok_bot import texts
from ostatok_bot.api import (
    ApiClient,
    ApiError,
    ApiUnavailable,
    Conflict,
    NotFound,
    TooManyRequests,
    Unauthorized,
    ValidationFailed,
)
from ostatok_bot.handlers import photo_supply
from ostatok_bot.handlers.photo_supply import (
    BUSY,
    DOWNLOAD_FAILED,
    FILE_TOO_BIG,
    NO_ANSWER,
    NOT_CONFIGURED,
    NOT_RECOGNIZED,
    OLD_SERVER,
    PHOTO_TOO_BIG,
    QUOTA,
    pick_photo,
)

from .conftest import CREAM, EGG, ME, PRODUCTS, SALMON, TARTARE

PROBLEM = {"content-type": "application/problem+json"}


def size(width, height, file_size, file_id=None):
    return PhotoSize(file_id=file_id or f"f{width}", file_unique_id=f"u{width}", width=width, height=height,
                     file_size=file_size)


TELEGRAM_SIZES = [size(90, 67, 1_500), size(320, 240, 15_000), size(800, 600, 80_000),
                  size(1280, 960, 190_000), size(2560, 1920, 1_400_000)]


ROWS = [
    {"line": 2, "productText": "Лосось охл.", "productId": SALMON["id"], "quantity": 2.4, "unit": "Kg",
     "shelfLifeHours": 48, "price": 4920, "status": "Ok", "message": "", "cost": 2050, "currentCost": 1900},
    {"line": 3, "productText": "Сливки 33", "productId": CREAM["id"], "quantity": 2, "unit": "L",
     "shelfLifeHours": None, "price": 760, "status": "Warning", "message": "Срок не указан, взят норматив.",
     "cost": 380, "currentCost": 380},
    {"line": 4, "productText": "Креветки тигровые", "productId": None, "quantity": 1, "unit": "Kg",
     "shelfLifeHours": None, "price": 1600, "status": "Error", "message": "Нет такого продукта в справочнике.",
     "cost": None, "currentCost": None},
]


@pytest.fixture
def keeper(h, monkeypatch):
    h.login("tok", role="Storekeeper")
    h.api.returns["list_products"] = PRODUCTS
    h.api.returns["list_supplies"] = []
    fetched = []

    async def fake_fetch(bot, file_id):
        fetched.append(file_id)
        return b"\xff\xd8\xff photo bytes"

    monkeypatch.setattr(photo_supply, "fetch_file", fake_fetch)
    h.fetched = fetched
    return h


# Выбор размера снимка


def test_pick_photo_takes_largest_size_within_limits():
    assert pick_photo(TELEGRAM_SIZES).width == 1280
    assert pick_photo([size(2560, 1920, 900_000), size(1600, 1200, 1_200_000), size(1000, 800, 300_000)]).width == 1000
    assert pick_photo([size(1600, 1200, 999_000)]).width == 1600
    assert pick_photo([size(2000, 1500, 500_000)]) is None


# Диалог


async def photo_to_number(h):
    h.api.returns["preview_supply_import"] = ROWS
    replies = await h.send_media(photo=TELEGRAM_SIZES)
    assert replies[0] == "Распознаю накладную, это может занять до минуты."
    assert replies[1] == (
        "Распознано строк: 3.\n\n"
        "Войдут в поставку:\n"
        "1. Лосось охлаждённый — 2,4 кг, срок 48 ч, 2 050 ₽ за кг (в справочнике 1 900 ₽, после приёмки станет 2 050 ₽)\n"
        "2. Сливки 33% — 2 л, срок по нормативу (72 ч), 380 ₽ за л\n"
        "   Внимание: Срок не указан, взят норматив.\n\n"
        "Не войдут, есть ошибки:\n"
        "Строка 4: «Креветки тигровые» — Нет такого продукта в справочнике.\n\n"
        "Номер поставки? Например, П-0413. Отменить: /cancel"
    )
    assert h.fetched == ["f1280"]
    assert h.api.called("preview_supply_import") == [("tok", b"\xff\xd8\xff photo bytes")]


async def test_photo_supply_full_flow(keeper):
    await photo_to_number(keeper)
    assert await keeper.send("З-1") == ["Номера на «З-», «Т-» и «Г-» заняты системой. Введите другой номер."]
    replies = await keeper.send("П-0500")
    assert replies[0].startswith("Поставка П-0500 по фото.\n\nРаспознано строк: 3.")
    assert replies[0].endswith("Строк с ошибками не войдёт: 1.\n\nПринять поставку?")
    keeper.api.returns["create_supply"] = {
        "id": "s1", "number": "П-0500", "supplier": None, "total": 5680, "items": [
            {"productName": "Лосось охлаждённый", "unit": "Kg", "quantity": 2.4, "expiresAt": "2026-10-14T09:00:00+00:00"},
            {"productName": "Сливки 33%", "unit": "L", "quantity": 2, "expiresAt": "2026-10-15T09:00:00+00:00"},
        ]}
    replies = await keeper.press("Принять поставку")
    assert replies[0] == "Отправляю поставку…"
    assert replies[1].startswith("Поставка П-0500 принята, на 5 680 ₽. Партии:")
    (call,) = keeper.api.called("create_supply")
    assert call[:4] == ("tok", "П-0500", [
        {"productId": SALMON["id"], "quantity": 2.4, "shelfLifeHours": 48, "cost": 2050},
        {"productId": CREAM["id"], "quantity": 2, "shelfLifeHours": None, "cost": 380},
    ], None)
    assert uuid.UUID(call[4])


async def test_photo_supply_409_asks_new_number_and_400(keeper):
    await photo_to_number(keeper)
    await keeper.send("П-1")
    keeper.api.returns["create_supply"] = Conflict(409)
    assert await keeper.press("Принять поставку") == [
        "Отправляю поставку…", "Поставка с номером П-1 уже есть. Введите другой номер."
    ]
    keeper.api.returns["create_supply"] = ValidationFailed(400, {"errors": {"items": ["Срок годности уже истёк."]}})
    await keeper.send("П-2")
    assert await keeper.press("Принять поставку") == [
        "Отправляю поставку…",
        "Поставка не принята.\nСрок годности уже истёк.\nИсправьте строки на сайте или примите поставку вручную: /supply",
    ]


@pytest.mark.parametrize(
    "error, expected",
    [
        (ApiUnavailable(503, {"detail": "не настроено"}, from_app=True), NOT_CONFIGURED),
        (TooManyRequests(429, {"detail": "Распознано уже 20 снимков за час. Подождите или загрузите файл."}),
         "Распознано уже 20 снимков за час. Подождите или загрузите файл. Или примите поставку вручную: /supply"),
        (TooManyRequests(429), QUOTA),
        (ApiUnavailable(502, {"detail": "Фото не удалось распознать."}, from_app=True), NOT_RECOGNIZED),
        (ApiError(413, {"detail": "Файл больше 1 МБ."}), PHOTO_TOO_BIG),
        (NotFound(404), OLD_SERVER),
        (ValidationFailed(400, {"detail": "На фото не нашлось строк с продуктами. Снимите накладную ближе и ровнее."}),
         "На фото не нашлось строк с продуктами. Снимите накладную ближе и ровнее.\nПришлите другое фото."),
        (ApiUnavailable(reason="ReadTimeout", unsure=True), NO_ANSWER),
    ],
)
async def test_photo_preview_errors(keeper, error, expected):
    keeper.api.returns["preview_supply_import"] = error
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)
    assert replies[-1] == expected
    assert keeper.api.called("create_supply") == []


@pytest.mark.parametrize("body", ["<html>Bad Gateway</html>", {"status": "error", "message": "upstream"}])
async def test_photo_proxy_failure_is_reported_as_api_down(keeper, body):
    # 502 от прокси хостинга, в том числе с JSON-телом, — не «фото не распозналось».
    keeper.api.returns["preview_supply_import"] = ApiUnavailable(502, body)
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)
    assert replies[-1] == texts.API_DOWN


async def test_only_errors_nothing_to_accept(keeper):
    keeper.api.returns["preview_supply_import"] = [ROWS[2]]
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)
    assert replies[-1].endswith("Принимать нечего. Исправьте справочник или примите поставку вручную: /supply")
    assert "Строка 4: «Креветки тигровые» — Нет такого продукта в справочнике." in replies[-1]
    assert await keeper.send("П-1") == ["Не понял. Список команд: /start"]


async def test_too_big_photo_and_documents(keeper):
    assert await keeper.send_media(photo=[size(2000, 1500, 600_000)]) == [PHOTO_TOO_BIG]
    big = Document(file_id="d1", file_unique_id="ud1", mime_type="image/jpeg", file_size=1_500_000)
    assert await keeper.send_media(document=big) == [FILE_TOO_BIG]
    pdf = Document(file_id="d2", file_unique_id="ud2", mime_type="application/pdf", file_size=100)
    assert await keeper.send_media(document=pdf) == ["Пришлите фото накладной или этикетки (JPEG, PNG или WebP)."]
    assert keeper.api.calls == []

    keeper.api.returns["preview_supply_import"] = ROWS
    small = Document(file_id="d3", file_unique_id="ud3", mime_type="image/png", file_size=400_000)
    await keeper.send_media(document=small)
    assert keeper.fetched == ["d3"]


async def test_photo_only_for_stock_roles(h, monkeypatch):
    monkeypatch.setattr(photo_supply, "fetch_file", lambda *a: pytest.fail("файл не должен скачиваться"))
    pdf = Document(file_id="d2", file_unique_id="ud2", mime_type="application/pdf", file_size=100)
    for role in ("Waiter", "Cook"):
        h.login("tok", role=role)
        for media in ({"photo": TELEGRAM_SIZES}, {"document": pdf}):
            assert await h.send_media(**media) == [
                "Нет доступа: принимать поставки можно только управляющему, шефу и кладовщику."
            ]
    assert h.api.calls == []


async def test_photo_on_supply_number_step_switches_to_photo_flow(keeper):
    await keeper.send("/supply")
    keeper.api.returns["preview_supply_import"] = ROWS
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)
    assert replies[-1].endswith("Номер поставки? Например, П-0413. Отменить: /cancel")
    assert await keeper.send("/cancel") == ["Действие отменено."]


# Клиент API


def client(handler):
    seen = []

    def wrapped(request):
        seen.append(request)
        return handler(request)

    http = httpx.AsyncClient(base_url="http://api", transport=httpx.MockTransport(wrapped))
    return ApiClient("http://api", http=http, retry_delays=(0, 0)), seen


async def test_preview_sends_raw_bytes():
    api, seen = client(lambda r: httpx.Response(200, json=ROWS))
    assert await api.preview_supply_import("t", b"\xff\xd8\xffdata") == ROWS
    request = seen[0]
    assert (request.method, request.url.path) == ("POST", "/api/supplies/import/preview")
    assert request.headers["content-type"] == "application/octet-stream"
    assert request.headers["authorization"] == "Bearer t"
    assert request.content == b"\xff\xd8\xffdata"


@pytest.mark.parametrize("status, exc", [(413, ApiError), (429, TooManyRequests), (400, ValidationFailed)])
async def test_preview_client_errors(status, exc):
    api, seen = client(lambda r: httpx.Response(status, json={"detail": "x"}, headers=PROBLEM))
    with pytest.raises(exc):
        await api.preview_supply_import("t", b"x")
    assert len(seen) == 1


@pytest.mark.parametrize("status", [502, 503])
async def test_app_502_503_are_not_retried(status):
    # Ответ приложения повтор не исправит, а снимок из лимита израсходует.
    api, seen = client(lambda r: httpx.Response(status, json={"detail": "x"}, headers=PROBLEM))
    with pytest.raises(ApiUnavailable) as info:
        await api.preview_supply_import("t", b"x")
    assert info.value.status == status and isinstance(info.value.body, dict)
    assert len(seen) == 1


async def test_proxy_502_is_retried():
    answers = iter([httpx.Response(502, text="<html>Bad Gateway</html>"), httpx.Response(200, json=[])])
    api, seen = client(lambda r: next(answers))
    assert await api.preview_supply_import("t", b"x") == []
    assert len(seen) == 2


async def test_supply_items_carry_cost():
    api, seen = client(lambda r: httpx.Response(201, json={"id": "s", "number": "П-1", "items": []}))
    await api.create_supply("t", "П-1", [
        {"productId": "p1", "quantity": 2.0, "shelfLifeHours": None, "cost": 2050.0},
        {"productId": "p2", "quantity": 1, "shelfLifeHours": 24, "cost": None},
    ])
    assert json.loads(seen[0].content)["items"] == [
        {"productId": "p1", "quantity": 2, "cost": 2050},
        {"productId": "p2", "quantity": 1, "shelfLifeHours": 24},
    ]


ACCEPTED = {"id": "o1", "number": 5, "status": "Accepted", "place": "", "total": 690, "items": [],
            "shortages": [], "writeOffs": [], "closedAt": None}


async def test_order_with_id_is_retried_on_timeout_with_same_id():
    calls = []

    def handler(request):
        calls.append(json.loads(request.content))
        if len(calls) == 1:
            raise httpx.ReadTimeout("slow", request=request)
        return httpx.Response(201, json=ACCEPTED)

    api, _ = client(handler)
    order_id = str(uuid.uuid4())
    assert (await api.create_order("t", [{"dishId": "d1", "quantity": 1}], None, order_id))["number"] == 5
    assert [c["id"] for c in calls] == [order_id, order_id]


async def test_order_without_id_is_not_retried_on_timeout():
    seen = []

    def handler(request):
        seen.append(request)
        raise httpx.ReadTimeout("slow", request=request)

    api, _ = client(handler)
    with pytest.raises(ApiUnavailable) as info:
        await api.create_order("t", [{"dishId": "d1", "quantity": 1}])
    assert info.value.unsure and len(seen) == 1


async def test_order_with_id_exhausted_retries_is_unsure():
    def handler(request):
        raise httpx.ReadTimeout("slow", request=request)

    api, seen = client(handler)
    with pytest.raises(ApiUnavailable) as info:
        await api.create_order("t", [{"dishId": "d1", "quantity": 1}], None, "id-1")
    assert info.value.unsure


async def test_hot_plan_path_and_404():
    api, seen = client(lambda r: httpx.Response(200, json={"portions": 3}))
    assert await api.get_hot_plan("t") == {"portions": 3}
    assert seen[0].url.path == "/api/hot-offers/plan"
    api, _ = client(lambda r: httpx.Response(404))
    assert await api.get_hot_plan("t") is None


# Заказ: «Повторить» отправляет тот же id


async def test_order_retry_button_reuses_order_id(h):
    h.login("tok", role="Waiter")
    h.api.returns.update(list_dishes=[TARTARE], get_stop_list=[], get_me={**ME, "places": []})
    await h.send("/order")
    await h.press("Тартар из лосося — 690 ₽")
    await h.send("1")
    await h.press("Готово")
    h.api.returns["create_order"] = ApiUnavailable(reason="ReadTimeout", unsure=True)
    replies = await h.press("Создать заказ")
    assert replies == [
        "Отправляю заказ…",
        "Сервер не ответил, заказ мог пройти. Нажмите «Повторить»: заказ уйдёт с тем же номером попытки и не задвоится."
    ]
    h.api.returns["create_order"] = {**ACCEPTED, "items": [{"dishId": TARTARE["id"], "dishName": "Тартар из лосося",
                                                          "quantity": 1, "discountPercent": 0, "discountQuantity": 0,
                                                          "price": 690}]}
    assert (await h.press("Повторить"))[1].startswith("Заказ №5 принят.")
    first, second = h.api.called("create_order")
    assert first[3] == second[3] and uuid.UUID(first[3])

    # Новая попытка заказа — новый id.
    await h.send("/order")
    await h.press("Тартар из лосося — 690 ₽")
    await h.send("1")
    await h.press("Готово")
    await h.press("Создать заказ")
    assert h.api.called("create_order")[2][3] != first[3]


# /hot: план спасения


HOT = [{"dishId": TARTARE["id"], "dishName": "Тартар из лосося", "photoUrl": None, "price": 690, "score": 1.0,
        "portions": 4, "availablePortions": 6, "valueAtRisk": 1200.0, "ingredients": [], "reason": "Лосось истекает.",
        "discount": None, "pinned": False}]
PLAN = {"atRiskValue": 11041.0, "plannedValue": 5616.0, "portions": 18, "leftovers": [
    {"productId": SALMON["id"], "productName": "Говяжья вырезка", "unit": "Kg", "quantity": 1.5, "hoursLeft": 4.7,
     "value": 825.0},
]}


async def test_hot_shows_plan(h):
    h.login("tok", role="Storekeeper")
    h.api.returns.update(get_hot_offers=HOT, get_hot_plan=PLAN)
    (text,) = await h.send("/hot")
    assert text.endswith(
        "План спасения: продать 18 порций — так в блюда уйдёт 5 616 ₽ из 11 041 ₽ истекающих продуктов.\n"
        "Не пристроено ни в одно блюдо:\n"
        "— Говяжья вырезка: 1,5 кг, осталось 4 ч, 825 ₽"
    )


async def test_hot_plan_without_costs_for_waiter(h):
    h.login("tok", role="Waiter")
    offers = [{**HOT[0], "valueAtRisk": None}]
    plan = {**PLAN, "atRiskValue": None, "plannedValue": None,
            "leftovers": [{**PLAN["leftovers"][0], "value": None}]}
    h.api.returns.update(get_hot_offers=offers, get_hot_plan=plan)
    (text,) = await h.send("/hot")
    assert "Тартар из лосося — продать 4 порции\nЛосось истекает." in text
    assert "План спасения: продать 18 порций.\n" in text
    assert text.endswith("— Говяжья вырезка: 1,5 кг, осталось 4 ч")
    assert "₽" not in text


async def test_hot_without_plan_still_shows_offers(h):
    h.login("tok", role="Chef")
    h.api.returns.update(get_hot_offers=HOT, get_hot_plan=None)
    assert (await h.send("/hot"))[0].endswith("Лосось истекает.")
    h.api.returns["get_hot_plan"] = ApiError(418)
    assert (await h.send("/hot"))[0].endswith("Лосось истекает.\n\nИтог плана спасения сейчас недоступен.")
    h.api.returns["get_hot_plan"] = {"portions": 3, "leftovers": [{"productName": "Без единицы"}]}
    assert (await h.send("/hot"))[0].endswith("Итог плана спасения сейчас недоступен.")
    h.api.returns["get_hot_plan"] = Unauthorized(401)
    assert (await h.send("/hot"))[0].startswith("Сессия закончилась")


# Роли без доступа к ценам: сервер присылает суммы пустыми


async def test_views_without_costs(h):
    h.login("tok", role="Cook")
    stock = [{"productId": SALMON["id"], "productName": "Лосось охлаждённый", "unit": "Kg", "fresh": 1.2,
              "expired": 0, "freshValue": None, "batches": 1, "nearestExpiry": None}]
    h.api.returns.update(get_stock=stock, get_batches=[], list_products=[{**EGG, "cost": None}])
    assert await h.send("/stock") == ["Остатки:\nЛосось охлаждённый — 1,2 кг"]
    assert (await h.send("/stock лосось"))[0].startswith("Лосось охлаждённый\nГодный остаток: 1,2 кг\n")
    assert await h.send("/products") == ["Продукты:\nЯйцо — шт, хранение 240 ч"]


def test_hot_plan_text_empty():
    assert texts.hot_plan_text(None) == ""
    assert texts.hot_plan_text({"atRiskValue": 0, "plannedValue": 0, "portions": 0, "leftovers": []}) == ""


def test_cost_unchanged_is_not_mentioned():
    products = {CREAM["id"]: {"name": "Сливки 33%", "unit": "L", "shelfLifeHours": 72}}
    assert photo_supply.row_line(ROWS[1], products).startswith("Сливки 33% — 2 л, срок по нормативу (72 ч), 380 ₽ за л\n")


async def test_norm_button_drops_recognized_shelf_life(keeper):
    # Так было на развёрнутом API: модель приняла дату накладной за «годен до», и у всех строк срок 5 ч.
    rows = [{**ROWS[0], "shelfLifeHours": 5}, ROWS[1]]
    keeper.api.returns["preview_supply_import"] = rows
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)
    assert "1. Лосось охлаждённый — 2,4 кг, срок 5 ч (норматив 48 ч), 2 050 ₽ за кг" in replies[-1]
    assert keeper.buttons() == ["Сроки по нормативу", "Отмена"]
    await keeper.send("П-7")
    assert keeper.buttons() == ["Принять поставку", "Сроки по нормативу", "Отмена"]
    replies = await keeper.press("Сроки по нормативу")
    assert "1. Лосось охлаждённый — 2,4 кг, срок по нормативу (48 ч)" in replies[0]
    assert keeper.buttons() == ["Принять поставку", "Отмена"]
    keeper.api.returns["create_supply"] = {"id": "s", "number": "П-7", "items": []}
    await keeper.press("Принять поставку")
    assert keeper.api.called("list_products")  # справочник спрошен до распознавания
    items = keeper.api.called("create_supply")[0][2]
    assert [i["shelfLifeHours"] for i in items] == [None, None]



async def test_second_photo_while_recognizing_does_not_start_another(keeper, monkeypatch):
    import asyncio

    release = asyncio.Event()

    async def slow_fetch(bot, file_id):
        await release.wait()
        return b"photo"

    monkeypatch.setattr(photo_supply, "fetch_file", slow_fetch)
    keeper.api.returns["preview_supply_import"] = ROWS
    first = asyncio.create_task(keeper.send_media(photo=TELEGRAM_SIZES))
    await asyncio.sleep(0)
    await asyncio.sleep(0)
    # Стенд собирает все сообщения бота за это время, в том числе ответ на первое фото.
    assert BUSY in await asyncio.wait_for(keeper.send_media(photo=TELEGRAM_SIZES), 5)
    release.set()
    await first
    assert len(keeper.api.called("preview_supply_import")) == 1


async def test_album_recognizes_only_first_page(keeper, monkeypatch):
    import asyncio

    release = asyncio.Event()

    async def slow_fetch(bot, file_id):
        await release.wait()
        return b"photo"

    monkeypatch.setattr(photo_supply, "fetch_file", slow_fetch)
    keeper.api.returns["preview_supply_import"] = ROWS
    first = asyncio.create_task(keeper.send_media(photo=TELEGRAM_SIZES, media_group_id="album-1"))
    await asyncio.sleep(0)
    await asyncio.sleep(0)
    second = keeper.send_media(photo=TELEGRAM_SIZES, media_group_id="album-1")
    assert BUSY not in await asyncio.wait_for(second, 5)   # молча
    release.set()
    replies = await first
    assert replies[0].endswith("Из альбома распознаю только первую страницу: остальные пришлите по одной после приёмки.")
    assert len(keeper.api.called("preview_supply_import")) == 1


async def test_failure_does_not_leave_dialog_recognizing(keeper):
    keeper.api.returns["preview_supply_import"] = ApiUnavailable(502, {"detail": "x"}, from_app=True)
    await keeper.send_media(photo=TELEGRAM_SIZES)
    keeper.api.returns["preview_supply_import"] = ROWS
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)   # не «подождите»
    assert replies[-1].endswith("Номер поставки? Например, П-0413. Отменить: /cancel")


async def test_download_failure(keeper, monkeypatch):
    from aiogram.exceptions import TelegramNetworkError
    from aiogram.methods import GetFile

    async def broken_fetch(bot, file_id):
        raise TelegramNetworkError(method=GetFile(file_id=file_id), message="timeout")

    monkeypatch.setattr(photo_supply, "fetch_file", broken_fetch)
    assert (await keeper.send_media(photo=TELEGRAM_SIZES))[-1] == DOWNLOAD_FAILED
    assert keeper.api.called("preview_supply_import") == []


async def test_command_on_number_step_is_a_command(keeper):
    await photo_to_number(keeper)
    replies = await keeper.send("/supply")
    assert replies[0].startswith("Новая поставка.")
    assert keeper.api.called("create_supply") == []


async def test_new_photo_on_confirm_restarts_recognition(keeper):
    await photo_to_number(keeper)
    await keeper.send("П-1")
    keeper.api.returns["preview_supply_import"] = [ROWS[1]]
    replies = await keeper.send_media(photo=TELEGRAM_SIZES)
    assert replies[-1].startswith("Распознано строк: 1.")


async def test_double_press_sends_once(keeper):
    await photo_to_number(keeper)
    await keeper.send("П-1")
    keeper.api.returns["create_supply"] = {"id": "s", "number": "П-1", "items": []}
    await keeper.press("Принять поставку")
    assert await keeper.click("ph:send") == ["Эта кнопка уже не работает. Начните действие заново."]
    assert len(keeper.api.called("create_supply")) == 1


def test_row_without_product_explains_why():
    row = {"line": 7, "productText": "Соль", "productId": None, "quantity": 1, "status": "Ok", "message": ""}
    assert photo_supply.row_problem(row) == "продукт не найден в справочнике"
    assert photo_supply.row_problem({**row, "productId": "p", "quantity": None}) == "не указано количество"


async def order_ready(h):
    h.login("tok", role="Waiter")
    h.api.returns.update(list_dishes=[TARTARE], get_stop_list=[], get_me={**ME, "places": []})
    await h.send("/order")
    await h.press("Тартар из лосося — 690 ₽")
    await h.send("1")
    await h.press("Готово")


async def test_order_server_error_has_no_endless_retry(h):
    await order_ready(h)
    h.api.returns["create_order"] = ApiUnavailable(500, {"detail": "Ошибка базы."}, from_app=True)
    assert await h.press("Создать заказ") == [
        "Отправляю заказ…", "Заказ не создан.\nОшибка базы.\nНачать заново: /order"
    ]
    assert "Повторить" not in h.buttons()


async def test_order_not_delivered_offers_retry(h):
    await order_ready(h)
    h.api.returns["create_order"] = ApiUnavailable(reason="ConnectError")
    replies = await h.press("Создать заказ")
    assert replies[1] == "Сервер недоступен, заказ не отправлен. Нажмите «Повторить», когда сервер ответит."
    assert h.buttons() == ["Повторить", "Отмена"]


async def test_order_double_press_sends_once(h):
    await order_ready(h)
    h.api.returns["create_order"] = {**ACCEPTED, "items": []}
    await h.press("Создать заказ")
    assert await h.click("or:send") == ["Эта кнопка уже не работает. Начните действие заново."]
    assert len(h.api.called("create_order")) == 1



async def test_order_timeout_then_app_error_is_unsure():
    # Первая попытка дошла (сервер создал заказ), но ответ не пришёл; вторая получила 500 от приложения.
    answers = iter(["timeout", httpx.Response(500, json={"detail": "x"}, headers=PROBLEM)])

    def handler(request):
        answer = next(answers)
        if answer == "timeout":
            raise httpx.ReadTimeout("slow", request=request)
        return answer

    api, _ = client(handler)
    with pytest.raises(ApiUnavailable) as info:
        await api.create_order("t", [{"dishId": "d1", "quantity": 1}], None, "id-1")
    assert info.value.unsure and info.value.from_app


async def test_order_unsure_app_error_keeps_retry_with_same_id(h):
    await order_ready(h)
    h.api.returns["create_order"] = ApiUnavailable(500, {"detail": "x"}, unsure=True, from_app=True)
    replies = await h.press("Создать заказ")
    assert replies[1].startswith("Сервер не ответил, заказ мог пройти.")
    h.api.returns["create_order"] = {**ACCEPTED, "items": []}
    await h.press("Повторить")
    first, second = h.api.called("create_order")
    assert first[3] == second[3]


async def test_cancel_during_recognition_drops_result(keeper, monkeypatch):
    import asyncio

    release, started = asyncio.Event(), asyncio.Event()

    async def slow_fetch(bot, file_id):
        started.set()
        await release.wait()
        return b"photo"

    monkeypatch.setattr(photo_supply, "fetch_file", slow_fetch)
    keeper.api.returns["preview_supply_import"] = ROWS
    task = asyncio.create_task(keeper.send_media(photo=TELEGRAM_SIZES))
    await asyncio.wait_for(started.wait(), 5)
    assert await keeper.send("/cancel") == ["Действие отменено."]
    release.set()
    replies = await asyncio.wait_for(task, 5)
    assert not any("Номер поставки?" in r for r in replies)
    assert await keeper.send("П-1") == ["Не понял. Список команд: /start"]


async def test_stream_error_while_downloading(keeper, monkeypatch):
    import aiohttp

    async def broken_fetch(bot, file_id):
        raise aiohttp.ClientPayloadError("обрыв")

    monkeypatch.setattr(photo_supply, "fetch_file", broken_fetch)
    assert (await keeper.send_media(photo=TELEGRAM_SIZES))[-1] == DOWNLOAD_FAILED
