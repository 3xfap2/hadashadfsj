"""Находки проверки 13.10: срок после вскрытия при правке продукта, команды на текстовых шагах,
инвентаризация при недоступном сервере, запоздалый ответ на нажатие, вход в отключённый ресторан,
сигналы после простоя, уведомления отозванному чату, вскрытие в журнале, длинные строки."""

from aiogram.exceptions import TelegramBadRequest
from aiogram.methods import AnswerCallbackQuery

from ostatok_bot.api import ApiUnavailable, Forbidden, Unauthorized
from ostatok_bot.handlers.inventory import InventoryForm
from ostatok_bot.handlers.supply import SupplyForm
from ostatok_bot.handlers.util import split_text
from ostatok_bot.monitor import alert_events
from ostatok_bot.storage import Storage
from ostatok_bot.texts import API_DOWN, SESSION_EXPIRED, journal_text

from .conftest import CREAM, EGG, PRODUCTS, TZ, FakeApi, batch, stop_item
from .test_monitor import REST_A, FakeBot, by_token, login, make_monitor
from .test_review_fixes import SUPPLY, fsm, supply_to_confirm


# Правка продукта не стирает срок после вскрытия


async def test_product_edit_keeps_opened_shelf_life(h):
    h.login("tok", role="Manager")
    h.api.returns["list_products"] = [{**CREAM, "openedShelfLifeHours": 24, "prepRecipe": []}, EGG]
    await h.send("/products")
    await h.press("Изменить")
    await h.press("Сливки 33% (л)")
    for _ in range(4):   # название, единица, срок, цена — всё «Оставить»
        await h.press("Оставить")
    h.api.returns["update_product"] = {**CREAM, "openedShelfLifeHours": 24}
    assert (await h.press("Сохранить"))[0].startswith("Сохранено")
    assert h.api.called("update_product") == [("tok", "p-cream", "Сливки 33%", "L", 72, 380, None, 24)]


# Команда на текстовом шаге — не значение поля


async def test_command_on_supply_number_step_starts_that_command(h):
    h.login("tok", role="Storekeeper")
    h.api.returns.update(list_products=PRODUCTS, get_batches=[])
    await h.send("/supply")
    assert await h.send("/inventory") == ["Какой продукт пересчитать?"]
    assert await fsm(h).get_state() == InventoryForm.product.state
    assert "number" not in await fsm(h).get_data()


async def test_command_on_supplier_step_is_not_a_supplier(h):
    h.login("tok", role="Storekeeper")
    h.api.returns.update(list_products=PRODUCTS, get_batches=[])
    await h.send("/supply")
    await h.send("П-1")
    assert await h.send("/inventory") == ["Какой продукт пересчитать?"]
    assert "supplier" not in await fsm(h).get_data()


async def test_command_on_product_name_step_is_not_a_name(h):
    h.login("tok", role="Manager")
    h.api.returns.update(list_products=PRODUCTS, list_supplies=[])
    await h.send("/products")
    await h.press("Добавить")
    replies = await h.send("/supply")
    assert replies[0].startswith("Новая поставка.")
    assert await fsm(h).get_state() == SupplyForm.number.state


async def test_supply_number_longer_than_server_allows_is_refused_at_once(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["list_products"] = PRODUCTS
    await h.send("/supply")
    assert await h.send("П" * 41) == ["Номер поставки — от 1 до 40 символов. Введите номер поставки."]
    assert await fsm(h).get_state() == SupplyForm.number.state


# Инвентаризация не теряется, если сервер недоступен


async def test_inventory_survives_unavailable_server(h):
    h.login("tok", role="Storekeeper")
    h.api.returns.update(list_products=PRODUCTS, get_batches=[])
    await h.send("/inventory")
    await h.press("Яйцо (шт)")
    await h.send("5")
    h.api.returns["inventory"] = ApiUnavailable(503)
    assert await h.press("Провести инвентаризацию") == [API_DOWN]
    assert await fsm(h).get_state() == InventoryForm.more.state

    h.api.returns["inventory"] = {"shortage": 0, "surplus": 0, "rows": []}
    assert await h.press("Провести инвентаризацию") == [
        "Инвентаризация проведена: расхождений нет, по факту всё совпало с учётом."
    ]
    assert h.api.called("inventory") == [("tok", [{"productId": "p-egg", "actual": 5}])] * 2
    assert await fsm(h).get_state() is None


# Ответ на заказ или поставку не сбрасывает диалог, начатый во время отправки


async def test_supply_success_keeps_dialog_started_while_sending(h):
    await supply_to_confirm(h)
    h.api.returns["get_batches"] = []

    async def slow_supply(*args):
        await h.send("/inventory")
        return SUPPLY

    h.api.returns["create_supply"] = slow_supply
    replies = await h.press("Принять поставку")
    assert replies[-1].startswith("Поставка П-1 принята")
    assert await fsm(h).get_state() == InventoryForm.product.state


# Запоздалый ответ на нажатие (после долгого запроса к API) не роняет обработчик


def telegram_refuses_answers(h):
    original = h.session.make_request

    async def refusing(bot, method, timeout=None):
        if isinstance(method, AnswerCallbackQuery):
            raise TelegramBadRequest(method, "query is too old and response timeout expired")
        return await original(bot, method, timeout)

    h.session.make_request = refusing


async def test_late_callback_answer_does_not_break_write_off(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["get_batches"] = [batch("b1")]
    telegram_refuses_answers(h)
    replies = await h.click("wo:b1")
    assert replies == ["Списать 0,9 л на 342 ₽: Сливки 33% (поставка П-0413)?"]
    assert h.button_data("Списать") == "woc:b1"


async def test_late_callback_answer_does_not_break_open(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["get_batches"] = [{**batch("b1", "Fresh", 30), "openedShelfLifeHours": 24, "openedAt": None}]
    telegram_refuses_answers(h)
    replies = await h.click("op:b1")
    assert replies[0].startswith("Вскрыть Сливки 33%")


# Вход в отключённый ресторан


async def test_login_to_disabled_restaurant_shows_server_text(h):
    detail = "Кабинет ресторана отключён. Напишите в поддержку «Остатка»."
    h.api.returns["login"] = Forbidden(403, {"detail": detail})
    await h.send("/login")
    await h.send("boss")
    assert await h.send("s3cret") == [detail]
    assert await fsm(h).get_state() is None


# Сигнал о ждущей поставке не вытесняется сигналами, которые бот считает сам


def test_supply_waiting_is_not_pushed_out_by_batch_alerts():
    alerts = [{"id": 1, "kind": "SupplyWaiting", "title": "Поставка П-7 ждёт подтверждения"}]
    alerts += [{"id": i, "kind": "BatchExpired", "title": "Просрочено"} for i in range(2, 9)]
    events = alert_events(alerts, 0)
    assert [e.kind for e in events] == ["supply_waiting"]


# Уведомления не уходят чату, чей токен отозван, даже если опрос идёт токеном коллеги


async def test_revoked_colleague_gets_no_notifications():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "good")       # опрос идёт токеном этого чата
    login(db, 2, "Storekeeper", REST_A, "revoked")
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = []
    api.returns["get_me"] = by_token({"good": {}, "revoked": Unauthorized(401)})
    monitor = make_monitor(api, db, bot)
    await monitor.poll_once()

    api.returns["get_stop_list"] = [stop_item(status="Stop")]
    await monitor.poll_once()
    assert len(bot.to(1)) == 1 and bot.to(1)[0][0].startswith("В стопе:")
    assert bot.to(2) == [(SESSION_EXPIRED + "\nПока вы не войдёте, уведомления не приходят.", None)]
    assert db.get_session(2) is None
    assert [a[0] for a in api.called("get_me")] == ["revoked"]   # токен опроса не перепроверяется


# Журнал: вскрытие


def test_journal_shows_opening_without_minus():
    movement = {"at": "2026-10-09T09:00:00+00:00", "type": "Opened", "productName": "Сливки 33%",
                "quantity": 0.5, "unit": "L", "note": "Упаковка вскрыта"}
    assert journal_text([movement], TZ) == "Последние операции:\n09.10 12:00 Вскрытие: Сливки 33% 0,5 л (Упаковка вскрыта)"


# Длинная строка режется под лимит Telegram


def test_split_text_cuts_a_single_long_line():
    text = "Остатки:\nНет на складе: " + ", ".join(["Продукт"] * 1200)
    chunks = split_text(text)
    assert all(len(c) <= 4000 for c in chunks)
    assert "".join(chunks[1:]) == text.split("\n")[1]
    assert chunks[0] == "Остатки:"



# «Закрыть стол» под старым вопросом не закрывает стол, выбранный позже


async def test_stale_close_button_does_not_close_other_table(h):
    from ostatok_bot.handlers.order import OTHER_TABLE

    from .test_roles import summary

    h.login("tok", role="Waiter")
    h.api.returns["get_orders_summary"] = summary(("Стол 1", 500), ("Стол 5", 900))
    await h.send("/close")
    await h.press("Стол 1 — 500 ₽")
    first = h.button_data("Закрыть стол")
    await h.send("/close")
    await h.press("Стол 5 — 900 ₽")
    assert await h.click(first) == [OTHER_TABLE]
    assert h.api.called("close_table") == []
    h.api.returns["close_table"] = {"place": "Стол 5", "orders": 1, "total": 900}
    assert await h.press("Закрыть стол") == ["Стол 5 закрыт: 1 заказ на 900 ₽."]
    assert h.api.called("close_table") == [("tok", "Стол 5")]


# Учётная запись поддержки в бот сотрудников не входит


async def test_support_account_is_not_signed_in(h):
    from ostatok_bot.api import LoginResult
    from ostatok_bot.handlers.auth import SUPPORT_REFUSED

    from .conftest import CHAT_ID, ME
    from .test_auth import EXPIRES

    h.api.returns["login"] = LoginResult("support-token", EXPIRES)
    h.api.returns["get_me"] = {**ME, "login": "support", "sections": [*ME["sections"], "support"]}
    await h.send("/login")
    await h.send("support")
    assert await h.send("pass-word") == [SUPPORT_REFUSED]
    assert h.deleted()                       # сообщение с паролем удалено
    assert h.db.get_session(CHAT_ID) is None  # токен не сохранён
    assert await fsm(h).get_state() is None


# Заготовка уходит с идентификатором попытки: при таймауте запрос повторяется с тем же id


async def test_prep_with_id_is_retried_with_the_same_id():
    import httpx

    from .test_api_client import body, client

    calls = iter([httpx.ReadTimeout("slow"), httpx.Response(201, json={"batchId": "b1", "shortages": []})])

    def handler(request):
        answer = next(calls)
        if isinstance(answer, Exception):
            raise answer
        return answer

    api, seen = client(handler)
    assert (await api.make_prep("t", "p1", 2, "prep-1"))["batchId"] == "b1"
    assert [body(r) for r in seen] == [{"productId": "p1", "quantity": 2, "id": "prep-1"}] * 2


# Внешняя поставка показывает дату, от которой считается срок


def test_incoming_text_shows_supply_date():
    from ostatok_bot.handlers.incoming import incoming_text

    supply = {"number": "ВН-1", "receivedAt": "2026-10-09T09:00:00+00:00", "createdAt": "2026-10-08T09:00:00+00:00",
              "total": 100, "items": []}
    assert "Дата поставки: 08.10 12:00, сроки считаются от неё." in incoming_text(supply, TZ)
