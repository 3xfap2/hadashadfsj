"""Находки ревью: привязка подтверждения к партии, отмена во время отправки, незавершённые отправки,
устойчивость монитора, сверка токена при удалении сессии, личные чаты, один экземпляр, не-JSON ответы,
границы чисел, вход, поздняя страница альбома."""

from datetime import timedelta

import httpx
import pytest
from aiogram.exceptions import TelegramForbiddenError, TelegramRetryAfter
from aiogram.fsm.storage.memory import MemoryStorage
from aiogram.methods import SendMessage
from aiogram.types import Chat

from ostatok_bot import monitor as monitor_module
from ostatok_bot.api import ApiClient, ApiUnavailable, LoginResult, TooManyRequests, Unauthorized
from ostatok_bot.app import lock_instance
from ostatok_bot.fmt import InputError, fmt_money, fmt_number, parse_money, parse_number, parse_positive
from ostatok_bot.handlers.common import SENDING
from ostatok_bot.handlers.order import ORDER_UNKNOWN, OrderForm
from ostatok_bot.handlers.submit import UNSURE
from ostatok_bot.handlers.supply import SupplyForm
from ostatok_bot.handlers.writeoff import OTHER_BATCH
from ostatok_bot.middleware import PRIVATE_ONLY
from ostatok_bot.monitor import Monitor
from ostatok_bot.storage import Storage
from ostatok_bot.texts import API_DOWN, LOGIN_REQUIRED, SESSION_EXPIRED, api_error_text

from .conftest import CHAT_ID, CREAM, ME, NOW, PRODUCTS, SALMON, TARTARE, FakeApi, batch, jwt, stop_item
from .test_monitor import REST_A, REST_B, FakeBot, by_token, login, make_monitor
from .test_photo_supply import ROWS, TELEGRAM_SIZES, keeper  # noqa: F401  (keeper — фикстура)

ORDER = {"id": "o1", "number": 5, "status": "Accepted", "place": None, "total": 690, "writeOffs": [],
         "items": [{"dishName": "Тартар из лосося", "quantity": 1}]}
SUPPLY = {"id": "s1", "number": "П-1", "supplier": None, "total": 760, "items": [
    {"productName": "Сливки 33%", "unit": "L", "quantity": 2, "expiresAt": "2026-10-15T09:00:00+00:00"}]}


def fsm(h):
    return h.dp.fsm.get_context(h.bot, CHAT_ID, CHAT_ID)


# 1. Подтверждение привязано к партии


async def test_write_off_confirm_is_bound_to_its_batch(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["get_batches"] = [batch("b1"), batch("b2", product=SALMON, remaining=2)]
    await h.click("wo:b1")
    first = h.button_data("Списать")
    await h.click("wo:b2")
    second = h.button_data("Списать")
    assert (first, second) == ("woc:b1", "woc:b2")

    # Кнопка под первым, уже устаревшим вопросом не списывает партию, выбранную позже.
    assert await h.click(first) == [OTHER_BATCH]
    assert h.api.called("write_off_batch") == []

    assert (await h.click(second))[-1] == "Списано: Лосось охлаждённый, 2 кг."
    assert h.api.called("write_off_batch") == [("tok", "b2")]


async def test_open_all_is_bound_to_its_batch(h):
    h.login("tok", role="Storekeeper")
    fresh = {"status": "Fresh", "hours": 30.0}
    opening = {"openedShelfLifeHours": 24, "openedAt": None}
    h.api.returns["get_batches"] = [{**batch("o1", **fresh), **opening},
                                    {**batch("o2", product=SALMON, **fresh), **opening}]
    h.api.returns["open_batch"] = {"expiresAt": "2026-10-10T09:00:00+00:00"}
    await h.click("op:o1")
    first = h.button_data("Всю партию")
    await h.click("op:o2")
    assert first == "opall:o1" and h.button_data("Всю партию") == "opall:o2"

    assert await h.click(first) == [OTHER_BATCH]
    assert h.api.called("open_batch") == []
    assert (await h.press("Всю партию"))[-1].startswith("Вскрыто: Лосось охлаждённый")
    assert h.api.called("open_batch") == [("tok", "o2", None)]


# 2. Отмена во время отправки, «Повторить», незавершённые отправки


async def order_to_confirm(h):
    h.login("tok", role="Waiter")
    h.api.returns.update(list_dishes=[TARTARE], get_stop_list=[], get_me={**ME, "places": []})
    await h.send("/order")
    await h.press("Тартар из лосося — 690 ₽")
    await h.send("1")
    await h.press("Готово")


async def supply_to_confirm(h):
    h.login("tok", role="Storekeeper")
    h.api.returns.update(list_products=PRODUCTS, list_supplies=[])
    await h.send("/supply")
    await h.send("П-1")
    await h.press("Без поставщика")
    await h.click(f"sp:p:{CREAM['id']}")
    await h.send("2")
    await h.press("По нормативу (72 ч)")
    await h.press("Готово")


async def test_cancel_while_order_is_being_sent_does_not_reset_dialog(h):
    await order_to_confirm(h)
    during = []

    async def slow_order(*args):
        during.extend(await h.send("/cancel"))
        during.extend(await h.click("cancel"))
        during.extend(await h.send("/order"))
        return ORDER

    h.api.returns["create_order"] = slow_order
    replies = await h.press("Создать заказ")
    assert during == [SENDING, SENDING, SENDING]
    assert replies[-1].startswith("Заказ №5 принят.")
    assert await fsm(h).get_state() is None


async def test_cancel_while_supply_is_being_sent_does_not_reset_dialog(h):
    await supply_to_confirm(h)
    during = []

    async def slow_supply(*args):
        during.extend(await h.send("/cancel"))
        during.extend(await h.send("/supply"))
        raise ApiUnavailable(reason="ReadTimeout", unsure=True)

    h.api.returns["create_supply"] = slow_supply
    replies = await h.press("Принять поставку")
    assert during == [SENDING, SENDING]
    # Диалог цел: данные поставки на месте, «Проверить и принять» отправит её с тем же id.
    assert replies[-1] == UNSURE
    assert await fsm(h).get_state() == SupplyForm.confirm.state
    h.api.returns["create_supply"] = SUPPLY
    assert (await h.press("Проверить и принять"))[-1].startswith("Поставка П-1 принята")
    first, second = h.api.called("create_supply")
    assert first[4] == second[4]


async def test_dialog_reset_while_sending_is_not_revived_without_data(h):
    await order_to_confirm(h)

    async def reset_then_timeout(*args):
        await h.send("/logout")   # диалог сброшен, пока запрос шёл
        raise ApiUnavailable(reason="ReadTimeout", unsure=True)

    h.api.returns["create_order"] = reset_then_timeout
    replies = await h.press("Создать заказ")
    assert replies[-1] == ORDER_UNKNOWN
    # Состояние не вернулось в «подтверждение» с пустыми данными.
    assert await fsm(h).get_state() is None


async def test_retry_without_order_data_does_not_crash(h):
    h.login("tok", role="Waiter")
    await fsm(h).set_state(OrderForm.confirm)
    assert await h.click("or:send") == ["Этот заказ уже не отправить. Начните заново: /order"]
    assert h.api.called("create_order") == []

    await fsm(h).set_state(SupplyForm.confirm)
    h.login("tok", role="Storekeeper")
    assert await h.click("sp:send") == ["Эту поставку уже не отправить. Начать заново: /supply"]
    assert h.api.called("create_supply") == []


async def test_unanswered_order_is_checked_after_restart(h):
    await order_to_confirm(h)
    h.api.returns["create_order"] = ApiUnavailable(reason="ReadTimeout", unsure=True)
    await h.press("Создать заказ")
    order_id = h.api.called("create_order")[0][3]
    assert h.db.get_pending(CHAT_ID, "order")["id"] == order_id

    h.dp.fsm.storage = MemoryStorage()   # перезапуск бота: диалог и кнопка «Повторить» потеряны
    h.api.returns["list_orders"] = [{**ORDER, "id": order_id}]
    replies = await h.send("/order")
    assert replies[0].startswith("Прошлый заказ дошёл до сервера, повторять его не нужно.\nЗаказ №5 принят.")
    assert "Новый заказ." in replies[0]
    assert h.db.get_pending(CHAT_ID, "order") is None


async def test_unanswered_order_that_was_not_created(h):
    await order_to_confirm(h)
    h.api.returns["create_order"] = ApiUnavailable(reason="ReadTimeout", unsure=True)
    await h.press("Создать заказ")
    h.dp.fsm.storage = MemoryStorage()
    h.api.returns["list_orders"] = [ORDER]   # чужой заказ с другим id
    replies = await h.send("/order")
    assert replies[0].startswith("Прошлый заказ, на который сервер не ответил, не создан")
    assert h.db.get_pending(CHAT_ID, "order") is None


async def test_answered_order_and_supply_leave_nothing_pending(h):
    await order_to_confirm(h)
    h.api.returns["create_order"] = ORDER
    await h.press("Создать заказ")
    assert h.db.get_pending(CHAT_ID, "order") is None
    assert h.api.called("list_orders") == []

    await supply_to_confirm(h)
    h.api.returns["create_supply"] = SUPPLY
    await h.press("Принять поставку")
    assert h.db.get_pending(CHAT_ID, "supply") is None


async def test_unanswered_supply_is_checked_on_next_supply(h):
    await supply_to_confirm(h)
    h.api.returns["create_supply"] = ApiUnavailable(reason="ReadTimeout", unsure=True)
    await h.press("Принять поставку")
    supply_id = h.api.called("create_supply")[0][4]
    assert h.db.get_pending(CHAT_ID, "supply") == {
        "id": supply_id, "data": {"number": "П-1"}, "at": h.db.get_pending(CHAT_ID, "supply")["at"]}

    h.dp.fsm.storage = MemoryStorage()
    h.api.returns["list_supplies"] = [{**SUPPLY, "id": supply_id}]
    replies = await h.send("/supply")
    assert replies[0].startswith("Прошлая поставка П-1 дошла до сервера, повторять её не нужно.\nПоставка П-1 принята")
    assert "Новая поставка." in replies[0]
    assert h.db.get_pending(CHAT_ID, "supply") is None

    # Поставка, которой на сервере нет: так и сказано, запись снята.
    h.db.save_pending(CHAT_ID, "supply", "missing-id", {"number": "П-2"})
    replies = await h.send("/supply")
    assert replies[0].startswith("Прошлая поставка П-2, на которую сервер не ответил, не принята")


def test_pending_survives_reopening_the_database(tmp_path):
    db = Storage(tmp_path / "bot.sqlite3")
    db.save_pending(7, "order", "id-1", {"items": []})
    db.close()
    db = Storage(tmp_path / "bot.sqlite3")
    assert db.get_pending(7, "order")["id"] == "id-1"
    assert db.get_pending(7, "supply") is None
    db.close()


# 3. Монитор: сбой одного ресторана и недоступный сервер


async def test_broken_restaurant_does_not_stop_the_others():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "token-a")
    login(db, 2, "Chef", REST_B, "token-b")
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = by_token({"token-a": [{"неожиданный": "ответ"}],
                                             "token-b": [stop_item(status="Available", portions=5)]})
    api.returns["get_batches"] = []
    assert await make_monitor(api, db, bot).poll_once() == {REST_A: [], REST_B: []}
    assert db.load_state("restaurant:" + REST_A) is None
    assert db.load_state("restaurant:" + REST_B) is not None   # второй ресторан опрошен и запомнен


async def test_corrupted_state_counts_as_first_run():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A)
    db.save_state("restaurant:" + REST_A, {"at": "не дата", "snapshot": None})
    api, bot = FakeApi(), FakeBot()
    api.returns.update(get_stop_list=[stop_item(status="Stop")], get_batches=[])
    assert await make_monitor(api, db, bot).poll_once() == {REST_A: []}
    assert db.load_state("restaurant:" + REST_A)["at"] == NOW.isoformat()


async def test_unavailable_server_stops_the_cycle_at_once():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "token-a")
    login(db, 2, "Chef", REST_B, "token-b")
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = ApiUnavailable(reason="ConnectTimeout")
    assert await make_monitor(api, db, bot).poll_once() == {REST_A: []}
    assert api.called("get_stop_list") == [("token-a",)]   # второй ресторан тот же таймаут не ждал


# 4. Монитор: доставка


class FlakyBot(FakeBot):
    def __init__(self, errors):
        super().__init__()
        self.errors = errors   # chat_id -> очередь исключений

    async def send_message(self, chat_id, text, reply_markup=None):
        if self.errors.get(chat_id):
            raise self.errors[chat_id].pop(0)
        await super().send_message(chat_id, text, reply_markup)


async def remembered(db, api, bot):
    api.returns.update(get_stop_list=[stop_item(status="Available", portions=5)], get_batches=[])
    monitor = make_monitor(api, db, bot)
    await monitor.poll_once()
    api.returns["get_stop_list"] = [stop_item(status="Stop")]
    return monitor


async def test_state_is_saved_only_after_a_message_was_delivered():
    db = Storage(":memory:")
    login(db, 1, "Waiter", REST_A)
    api, bot = FakeApi(), FlakyBot({1: [RuntimeError("сеть")]})
    monitor = await remembered(db, api, bot)
    before = db.load_state("restaurant:" + REST_A)

    await monitor.poll_once()             # Telegram не принял сообщение
    assert bot.sent == [] and db.load_state("restaurant:" + REST_A) == before

    await monitor.poll_once()             # событие не потеряно: пришло со следующим опросом
    assert [text for text, _ in bot.to(1)] == ["В стопе: Тартар из лосося — просрочен продукт «Лосось охлаждённый»"]
    assert db.load_state("restaurant:" + REST_A)["snapshot"]["dishes"] == {TARTARE["id"]: "Stop"}
    await monitor.poll_once()
    assert len(bot.sent) == 1


async def test_blocked_bot_removes_the_session():
    db = Storage(":memory:")
    login(db, 1, "Waiter", REST_A)
    login(db, 2, "Waiter", REST_A)
    blocked = TelegramForbiddenError(method=SendMessage(chat_id=1, text="x"), message="Forbidden: bot was blocked by the user")
    api, bot = FakeApi(), FlakyBot({1: [blocked]})
    monitor = await remembered(db, api, bot)
    await monitor.poll_once()
    assert db.get_session(1) is None and db.get_session(2) is not None
    assert len(bot.to(2)) == 1


async def test_retry_after_waits_and_sends_again(monkeypatch):
    slept = []

    async def fake_sleep(seconds):
        slept.append(seconds)

    monkeypatch.setattr(monitor_module.asyncio, "sleep", fake_sleep)
    db = Storage(":memory:")
    login(db, 1, "Waiter", REST_A)
    flood = TelegramRetryAfter(method=SendMessage(chat_id=1, text="x"), message="Too Many Requests", retry_after=7)
    api, bot = FakeApi(), FlakyBot({1: [flood]})
    monitor = await remembered(db, api, bot)
    await monitor.poll_once()
    assert slept == [7] and len(bot.to(1)) == 1


# 5. Удаление сессии только со своим токеном


def test_delete_session_checks_token():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "new")
    assert db.delete_session(1, "old") is False and db.get_session(1).token == "new"
    assert db.delete_session(1, "new") is True and db.get_session(1) is None
    assert db.delete_session(1) is False


async def test_monitor_does_not_erase_a_fresh_login():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "old")
    api, bot = FakeApi(), FakeBot()

    def stop_list(token):
        login(db, 1, "Chef", REST_A, "new")   # пока шёл запрос со старым токеном, чат вошёл заново
        raise Unauthorized(401)

    api.returns["get_stop_list"] = stop_list
    await Monitor(api, db, bot, 30, clock=lambda: NOW).poll_once()
    assert db.get_session(1).token == "new"
    assert bot.sent == [] and bot.commands_reset == []


async def test_handler_401_for_an_old_token_keeps_the_new_session(h):
    h.login("new-token")
    stale = Unauthorized(401)
    stale.token = "old-token"
    h.api.returns["get_stop_list"] = stale
    assert await h.send("/stoplist") == ["Вход в этом чате обновился, пока шёл запрос. Повторите действие."]
    assert h.db.get_session(CHAT_ID).token == "new-token"

    current = Unauthorized(401)
    current.token = "new-token"
    h.api.returns["get_stop_list"] = current
    assert await h.send("/stoplist") == [SESSION_EXPIRED]
    assert h.db.get_session(CHAT_ID) is None


async def test_api_error_carries_the_request_token():
    api = ApiClient("http://api", retry_delays=(), http=httpx.AsyncClient(
        base_url="http://api", transport=httpx.MockTransport(lambda request: httpx.Response(401, json={}))))
    with pytest.raises(Unauthorized) as caught:
        await api.get_stop_list("the-token")
    assert caught.value.token == "the-token"
    await api.close()


# 6. Только личные чаты, один экземпляр


async def test_groups_are_refused(h):
    h.login("tok")
    h.chat = Chat(id=-1001, type="supergroup")
    assert await h.send("/login") == [PRIVATE_ONLY]
    assert await h.send("/stoplist@ostatok_bot") == [PRIVATE_ONLY]
    assert await h.send("просто разговор в группе") == []      # на обычные сообщения группы бот молчит
    assert await h.click("wo:b1") == [PRIVATE_ONLY]
    assert h.api.calls == [] and await h.dp.fsm.get_context(h.bot, -1001, CHAT_ID).get_state() is None


def test_second_instance_is_refused(tmp_path):
    first = lock_instance(tmp_path / "data")
    with pytest.raises(SystemExit, match="Бот уже запущен на этом компьютере"):
        lock_instance(tmp_path / "data")
    first.close()
    lock_instance(tmp_path / "data").close()   # после выхода первого экземпляра запуск снова возможен


# 7. Успешный статус без JSON и ошибка показа после успеха


@pytest.mark.parametrize("response", [
    httpx.Response(201, html="<html>Service waking up</html>"),
    httpx.Response(200, content=b""),
    httpx.Response(200, json="ok"),
])
async def test_success_without_json_object_is_unknown_result(response):
    api = ApiClient("http://api", retry_delays=(), http=httpx.AsyncClient(
        base_url="http://api", transport=httpx.MockTransport(lambda request: response)))
    with pytest.raises(ApiUnavailable) as caught:
        await api.create_order("t", [{"dishId": "d", "quantity": 1}], None, "id-1")
    assert caught.value.unsure
    with pytest.raises(ApiUnavailable) as caught:
        await api.create_supply("t", "П-1", [{"productId": "p", "quantity": 1}], None, "id-2")
    assert caught.value.unsure
    await api.close()


async def test_render_failure_after_success_says_done(h):
    await order_to_confirm(h)
    h.api.returns["create_order"] = {"status": "Accepted"}   # сервер ответил успехом, но не тем, что бот ждал
    replies = await h.press("Создать заказ")
    assert replies[-1] == ("Заказ отправлен, сервер ответил успехом. Показать ответ сервера не удалось: "
                           "проверьте результат в /journal, прежде чем повторять.")
    assert await fsm(h).get_state() is None

    await supply_to_confirm(h)
    h.api.returns["create_supply"] = {"items": [{}]}
    assert (await h.press("Принять поставку"))[-1].startswith("Поставка отправлена, сервер ответил успехом.")


# 8. Границы чисел


@pytest.mark.parametrize("text", ["1e3", "1E3", "1_000", "1e25", "1000001", "9" * 30])
def test_parse_positive_rejects_exponent_underscore_and_huge(text):
    with pytest.raises(InputError):
        parse_positive(text)


def test_number_limits():
    assert parse_positive("1000000") == 1_000_000
    assert parse_money("10000000") == 10_000_000
    with pytest.raises(InputError, match="не больше 10 000 000"):
        parse_money("10000001")
    with pytest.raises(InputError, match="не больше 1 000 000"):
        parse_number("-1000001")
    # Форматирование не падает и на числе, которое пришло не от пользователя.
    assert fmt_number(1e25) and fmt_money(1e30)


async def test_huge_quantity_does_not_break_supply_dialog(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["list_products"] = PRODUCTS
    await h.send("/supply")
    await h.send("П-1")
    await h.press("Без поставщика")
    await h.click(f"sp:p:{CREAM['id']}")
    assert (await h.send("1e25"))[0].startswith("«1e25» не похоже на число.")
    assert (await h.send("99999999999999999999999999"))[0].startswith("Слишком большое число")
    assert await fsm(h).get_state() == SupplyForm.quantity.state


def test_429_has_a_human_text():
    assert api_error_text(TooManyRequests(429)) == "Слишком много запросов к серверу. Подождите минуту и повторите."


# 10. Вход: состояние очищается и при сбое после принятого пароля


async def test_login_state_is_cleared_when_me_fails(h):
    h.api.returns["login"] = LoginResult(jwt(), NOW + timedelta(hours=8))
    h.api.returns["get_me"] = ApiUnavailable(reason="ReadTimeout")
    await h.send("/login")
    await h.send("demo")
    assert (await h.send("demo2026"))[-1] == API_DOWN
    assert await fsm(h).get_state() is None
    # Следующее сообщение — уже не пароль.
    assert await h.send("ещё раз") == [LOGIN_REQUIRED]
    assert len(h.api.called("login")) == 1


# 12. Поздняя страница альбома


async def test_late_album_page_does_not_start_second_recognition(keeper):  # noqa: F811
    keeper.api.returns["preview_supply_import"] = ROWS
    replies = await keeper.send_media(photo=TELEGRAM_SIZES, media_group_id="album-7")
    assert replies[-1].startswith("Распознано строк: 3.")

    # Вторая страница пришла, когда первая уже распознана: ни распознавания, ни сброса диалога.
    assert await keeper.send_media(photo=TELEGRAM_SIZES, media_group_id="album-7") == []
    assert len(keeper.api.called("preview_supply_import")) == 1
    assert (await keeper.send("П-0500"))[0].startswith("Поставка П-0500 по фото.")

    # Отдельное фото и новый альбом по-прежнему распознаются.
    await keeper.send_media(photo=TELEGRAM_SIZES, media_group_id="album-8")
    assert len(keeper.api.called("preview_supply_import")) == 2
