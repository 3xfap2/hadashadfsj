"""Изменения API после проверки: поставки на подтверждение, сигналы сервера, итоги заказов,
ошибки 409 с текстом, повторы запросов к засыпающему серверу."""

from datetime import datetime, timedelta

import httpx
import pytest

from ostatok_bot.api import ApiClient, ApiUnavailable, Conflict, NotFound
from ostatok_bot.monitor import Monitor, alert_events
from ostatok_bot.storage import Storage
from ostatok_bot.texts import API_NO_ANSWER, journal_text

from .conftest import CREAM, NOW, PRODUCTS, ROLE_SECTIONS, TZ, FakeApi
from .test_monitor import REST_A, FakeBot, login


# Повторы запросов


def client(responses):
    """Транспорт отдаёт ответы по очереди; исключение в списке — сбой сети."""
    seen = []

    def handler(request):
        seen.append(request)
        value = responses.pop(0)
        if isinstance(value, Exception):
            raise value
        return value

    http = httpx.AsyncClient(base_url="http://api", transport=httpx.MockTransport(handler))
    return ApiClient("http://api", http=http, retry_delays=(0, 0)), seen


async def test_get_is_repeated_on_502_and_timeout():
    api, seen = client([httpx.Response(502), httpx.ReadTimeout("slow"), httpx.Response(200, json=[])])
    assert await api.get_stop_list("t") == []
    assert len(seen) == 3


async def test_get_gives_up_after_retries():
    api, seen = client([httpx.Response(502)] * 3)
    with pytest.raises(ApiUnavailable):
        await api.get_stop_list("t")
    assert len(seen) == 3


async def test_post_is_repeated_when_it_did_not_reach_the_app():
    api, seen = client([httpx.ConnectError("refused"), httpx.Response(503), httpx.Response(200, json={"status": "Accepted"})])
    assert (await api.create_order("t", [{"dishId": "d", "quantity": 1}]))["status"] == "Accepted"
    assert len(seen) == 3


async def test_post_is_not_repeated_after_timeout():
    # Заказ мог пройти: повтор списал бы продукты второй раз.
    api, seen = client([httpx.ReadTimeout("slow")])
    with pytest.raises(ApiUnavailable) as caught:
        await api.create_order("t", [{"dishId": "d", "quantity": 1}])
    assert caught.value.unsure and len(seen) == 1


async def test_500_is_not_repeated():
    api, seen = client([httpx.Response(500)])
    with pytest.raises(ApiUnavailable):
        await api.get_stop_list("t")
    assert len(seen) == 1


# Новые адреса API


async def test_incoming_endpoints_and_summary_since_in_utc():
    api, seen = client([
        httpx.Response(200, json=[]),
        httpx.Response(200, json={"id": "s1"}),
        httpx.Response(204),
        httpx.Response(200, json={"tables": []}),
    ])
    await api.list_incoming("t")
    await api.accept_incoming("t", "s1")
    await api.reject_incoming("t", "s2")
    await api.get_orders_summary("t", since=datetime(2026, 10, 11, 0, 0, tzinfo=TZ))
    assert [(r.method, r.url.path) for r in seen] == [
        ("GET", "/api/supplies/incoming"),
        ("POST", "/api/supplies/incoming/s1/accept"),
        ("POST", "/api/supplies/incoming/s2/reject"),
        ("GET", "/api/orders/summary"),
    ]
    # Со смещением часового пояса сервер отвечает 500, поэтому только UTC.
    assert seen[3].url.params["since"] == "2026-10-10T21:00:00Z"


async def test_update_keeps_opened_shelf_life_and_409_text_is_read():
    api, seen = client([httpx.Response(409, json={"detail": "Единицу измерения нельзя сменить."})])
    with pytest.raises(Conflict) as caught:
        await api.update_product("t", "p1", "Сливки", "Kg", 72, 380, None, 24)
    assert caught.value.detail == "Единицу измерения нельзя сменить."
    assert b'"openedShelfLifeHours":24' in seen[0].content.replace(b" ", b"")


# /incoming

WAITING = {
    "id": "in-1", "number": "EDI-77", "supplier": "Фермер Иванов", "receivedAt": "2026-10-09T09:30:00+00:00",
    "total": 1140,
    "items": [{"productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "quantity": 3, "shelfLifeHours": 72}],
}
WAITING_TEXT = (
    "Поставка EDI-77, поставщик Фермер Иванов\nПришла 09.10 12:30, ждёт подтверждения.\n"
    "1. Сливки 33% — 3 л, срок 72 ч\nСумма: 1 140 ₽"
)


@pytest.fixture
def keeper(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["list_products"] = PRODUCTS
    h.api.returns["list_incoming"] = [WAITING]
    return h


async def test_incoming_list_with_buttons(keeper):
    assert await keeper.send("/incoming") == ["Ждёт подтверждения одна поставка.", WAITING_TEXT]
    assert keeper.buttons() == ["Принять", "Отклонить", "Принять частично"]


async def test_incoming_empty(keeper):
    keeper.api.returns["list_incoming"] = []
    assert await keeper.send("/incoming") == ["Поставок, которые ждут подтверждения, нет."]


async def test_incoming_accept(keeper):
    await keeper.send("/incoming")
    keeper.api.returns["accept_incoming"] = {
        "id": "in-1", "number": "EDI-77", "supplier": "Фермер Иванов", "createdAt": "2026-10-09T10:00:00+00:00",
        "total": 1140, "items": [{"batchId": "b9", "productId": "p-cream", "productName": "Сливки 33%", "unit": "L",
                                  "quantity": 3, "remaining": 3, "expiresAt": "2026-10-12T10:00:00+00:00"}],
    }
    replies = await keeper.press("Принять")
    assert replies[0].startswith("Поставка EDI-77 принята")
    assert keeper.api.called("accept_incoming") == [("tok", "in-1")]


async def test_incoming_accept_when_already_decided_or_number_taken(keeper):
    await keeper.send("/incoming")
    keeper.api.returns["accept_incoming"] = NotFound(404)
    assert await keeper.press("Принять") == ["Эту поставку уже приняли или отклонили. Список: /incoming"]

    await keeper.send("/incoming")
    keeper.api.returns["accept_incoming"] = Conflict(409, {"detail": "Поставка с номером EDI-77 уже принята. Отклоните эту."})
    assert await keeper.press("Принять") == ["Поставка с номером EDI-77 уже принята. Отклоните эту."]
    assert keeper.buttons() == ["Отклонить"]


async def test_incoming_reject_asks_first(keeper):
    await keeper.send("/incoming")
    replies = await keeper.press("Отклонить")
    assert replies[0].endswith("Отклонить поставку? На склад она не попадёт, вернуть её из бота нельзя.")
    assert keeper.api.called("reject_incoming") == []

    assert await keeper.press("Не отклонять") == [WAITING_TEXT]
    await keeper.press("Отклонить")
    assert await keeper.press("Да, отклонить") == ["Поставка отклонена, на склад она не попала."]
    assert keeper.api.called("reject_incoming") == [("tok", "in-1")]


@pytest.mark.parametrize("role", ["Waiter", "Cook"])
async def test_incoming_is_for_stock_roles_only(h, role):
    h.login("tok", role=role)
    assert await h.send("/incoming") == [
        "Нет доступа: принимать поставки можно только управляющему, шефу и кладовщику."
    ]
    assert h.api.called("list_incoming") == []


# Ошибки сервера


async def test_unit_change_conflict_returns_to_unit_choice(h):
    from .test_actions import fill_edit_product

    h.login("tok")
    h.api.returns["list_products"] = PRODUCTS
    await fill_edit_product(h)
    h.api.returns["update_product"] = Conflict(
        409, {"detail": "Единицу измерения нельзя сменить: продукт уже есть в поставках или техкартах."}
    )
    replies = await h.press("Сохранить")
    assert replies[0].startswith("Единицу измерения нельзя сменить: продукт уже есть в поставках или техкартах.\n\n"
                                 "Единица измерения?")
    assert "кг" in h.buttons()


async def test_unanswered_change_is_explained(h):
    h.login("tok", role="Waiter")
    h.api.returns["get_orders_summary"] = ApiUnavailable(reason="ReadTimeout", unsure=True)
    assert await h.send("/close") == [API_NO_ANSWER]


def test_inventory_movements_in_journal():
    moves = [
        {"at": "2026-10-09T09:00:00+00:00", "type": "Surplus", "productName": "Сливки 33%", "unit": "L",
         "quantity": 0.5, "note": "Инвентаризация"},
        {"at": "2026-10-09T09:00:00+00:00", "type": "Shortage", "productName": "Яйцо", "unit": "Pcs",
         "quantity": -4, "note": "Инвентаризация"},
    ]
    text = journal_text(moves, TZ)
    assert "Излишек: Сливки 33% +0,5 л" in text
    assert "Недостача: Яйцо −4 шт" in text


# Сигналы сервера в уведомлениях


def alert(id_, kind, title="Заголовок", detail="Подробности"):
    return {"id": id_, "at": "2026-10-09T11:00:00+00:00", "kind": kind, "title": title, "detail": detail,
            "expiresAt": None, "read": False}


def test_first_poll_only_remembers_alerts():
    assert alert_events([alert(5, "SupplyWaiting")], None) == []


def test_alerts_the_bot_computes_itself_are_skipped():
    alerts = [alert(i, kind) for i, kind in enumerate(["BatchExpiring", "BatchExpired", "DishStopped", "DishBack"], 2)]
    assert alert_events(alerts, 1) == []


def test_supply_waiting_and_unknown_kinds():
    events = alert_events([
        alert(4, "Inventory", "Инвентаризация завершена", ""),
        alert(3, "SupplyWaiting", "Поставка EDI-77 ждёт подтверждения", "Фермер Иванов · позиций: 1"),
        alert(2, "SupplyWaiting"),   # уже видели
    ], 2)
    assert [(e.kind, e.text) for e in events] == [
        ("supply_waiting", "Поставка EDI-77 ждёт подтверждения\nФермер Иванов · позиций: 1\nПринять или отклонить: /incoming"),
        ("alert", "Инвентаризация завершена"),
    ]


def test_odd_alerts_do_not_break_anything():
    events = alert_events([{"id": 7, "kind": 99}, {"kind": "X"}, alert(8, "New", "", "")], 1)
    assert [e.text for e in events] == ["Новый сигнал", "Новый сигнал"]


async def test_supply_waiting_goes_to_stock_roles_and_unknown_to_everyone():
    db = Storage(":memory:")
    login(db, 1, "Waiter", REST_A)
    login(db, 2, "Storekeeper", REST_A)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = []
    api.returns["get_batches"] = []
    alerts = {"unread": 0, "items": [alert(10, "DishBack")]}
    api.returns["get_alerts"] = lambda token, take: alerts
    monitor = Monitor(api, db, bot, 30, clock=lambda: NOW)

    await monitor.poll_once()
    assert bot.sent == []

    alerts["items"] = [alert(12, "Mystery", "Что-то новое", "Текст"),
                       alert(11, "SupplyWaiting", "Поставка 5 ждёт подтверждения", "позиций: 2"), alert(10, "DishBack")]
    await monitor.poll_once()
    assert [t for t, _ in bot.to(1)] == ["Что-то новое\nТекст"]
    assert [t for t, _ in bot.to(2)] == [
        "Поставка 5 ждёт подтверждения\nпозиций: 2\nПринять или отклонить: /incoming\nЧто-то новое\nТекст"
    ]

    await monitor.poll_once()   # повтор: ничего нового
    assert len(bot.sent) == 2


async def test_alerts_survive_bot_downtime_and_failures():
    db = Storage(":memory:")
    login(db, 2, "Storekeeper", REST_A)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = []
    api.returns["get_batches"] = []
    api.returns["get_alerts"] = {"unread": 0, "items": [alert(1, "DishBack")]}
    clock = [NOW]
    monitor = Monitor(api, db, bot, 30, clock=lambda: clock[0])
    await monitor.poll_once()

    api.returns["get_alerts"] = ApiUnavailable(502)   # сигналы не пришли — остальной опрос идёт
    await monitor.poll_once()
    assert bot.sent == []

    # Бот лежал 40 минут: снимок стоп-листа устарел, но новые сигналы всё равно приходят.
    clock[0] = NOW + timedelta(minutes=40)
    api.returns["get_alerts"] = {"unread": 1, "items": [alert(2, "SupplyWaiting", "Поставка 9 ждёт подтверждения", "")]}
    await monitor.poll_once()
    assert [t for t, _ in bot.to(2)] == ["Поставка 9 ждёт подтверждения\nПринять или отклонить: /incoming"]
