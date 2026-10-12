from datetime import datetime, timedelta, timezone

import pytest

from ostatok_bot.api import ApiUnavailable, LoginResult, TooManyRequests, Unauthorized, ValidationFailed
from ostatok_bot.storage import Storage

from aiogram.methods import DeleteMyCommands, SetMyCommands

from ostatok_bot.texts import SESSION_EXPIRED

from .conftest import CHAT_ID, PRODUCTS, RESTAURANT, jwt

CHEF_WELCOME = (
    "Вы вошли: demo, Шеф-повар, Демо-ресторан.\n"
    "Доступно: /stoplist, /expiring, /stock, /supply, /incoming, /inventory, /preps, /dishes, /products, /hot, /buy, /losses, "
    "/stopcost, /journal"
)

EXPIRES = datetime.now(timezone.utc) + timedelta(hours=8)


async def start_login(h, login="admin"):
    assert await h.send("/login") == ["Введите логин. Отменить: /cancel"]
    assert await h.send(login) == ["Введите пароль. Сообщение с паролем я сразу удалю."]


async def test_password_message_is_deleted_before_api_call(h):
    order = []
    h.api.returns["login"] = lambda *a: order.append("api") or LoginResult("tok", EXPIRES)
    original = h.session.make_request

    async def tracking(bot, method, timeout=None):
        if type(method).__name__ == "DeleteMessage":
            order.append("delete")
        return await original(bot, method, timeout)

    h.session.make_request = tracking
    await start_login(h)
    replies = await h.send("s3cret")

    assert order == ["delete", "api"]
    assert h.deleted()[0].chat_id == CHAT_ID
    assert h.api.called("login") == [("admin", "s3cret")]
    assert replies == [CHEF_WELCOME]
    session = h.db.get_session(CHAT_ID)
    assert session.token == "tok"
    assert session.expires_at == EXPIRES


async def test_password_is_not_stored(tmp_path, h):
    h.db = Storage(tmp_path / "bot.sqlite3")
    h.dp.workflow_data["db"] = h.db
    h.api.returns["login"] = LoginResult("tok", EXPIRES)
    await start_login(h)
    await h.send("UniquePassw0rd")
    h.db.close()
    assert b"UniquePassw0rd" not in (tmp_path / "bot.sqlite3").read_bytes()


async def test_if_delete_fails_user_is_warned_and_login_continues(h):
    h.session.fail_delete = True
    h.api.returns["login"] = LoginResult("tok", EXPIRES)
    await start_login(h)
    replies = await h.send("pwd")
    assert replies[0] == "Не получилось удалить сообщение с паролем, удалите его сами."
    assert replies[-1].startswith("Вы вошли")


@pytest.mark.parametrize(
    "command", ["/stoplist", "/expiring", "/stock", "/hot", "/journal", "/supply", "/order",
                "/products", "/dishes", "/logout", "просто текст"],
)
async def test_commands_without_login_are_rejected(h, command):
    assert await h.send(command) == ["Войдите через /login"]
    assert h.api.calls == []


async def test_buttons_without_login_are_rejected(h):
    assert await h.click("wo:b1") == ["Войдите через /login"]
    assert h.api.calls == []


async def test_start_works_without_login(h):
    replies = await h.send("/start")
    assert "/login" in replies[0]


async def test_expired_token_asks_to_login_again(h):
    h.db.save_session(CHAT_ID, "old", datetime.now(timezone.utc) - timedelta(minutes=1))
    assert await h.send("/stoplist") == [SESSION_EXPIRED]
    assert h.db.get_session(CHAT_ID) is None
    assert h.api.calls == []


async def test_api_401_during_action_asks_to_login_again(h):
    h.login()
    h.api.returns["list_products"] = Unauthorized(401)
    assert await h.send("/products") == [SESSION_EXPIRED]
    assert h.db.get_session(CHAT_ID) is None


async def test_user_actions_use_user_token(h):
    h.login("my-token")
    h.api.returns["list_products"] = PRODUCTS
    await h.send("/products")
    assert h.api.called("list_products") == [("my-token",)]


@pytest.mark.parametrize(
    "error, expected",
    [
        (Unauthorized(401, {"title": "Unauthorized", "detail": "Неверный логин или пароль.", "attemptsLeft": 4}),
         "Неверный логин или пароль. Попыток осталось: 4. Попробуйте ещё раз: /login"),
        (Unauthorized(401, {"detail": "Неверный логин или пароль.", "attemptsLeft": 1}),
         "Неверный логин или пароль. Попыток осталось: 1. Попробуйте ещё раз: /login"),
        (Unauthorized(401),
         "Неверный логин или пароль. Попробуйте ещё раз: /login"),
        (TooManyRequests(429, {"detail": "Слишком много неверных попыток.", "retryAfterMinutes": 10}),
         "Слишком много неверных попыток. Вход откроется через 10 мин."),
        # общий предел запросов с одного адреса отвечает 429 без retryAfterMinutes
        (TooManyRequests(429),
         "Слишком много попыток входа. Подождите минуту и попробуйте снова: /login"),
        (ValidationFailed(400, {"errors": {"login": ["Логин обязателен"]}}),
         "Логин обязателен\nПопробуйте ещё раз: /login"),
    ],
)
async def test_login_errors_show_server_details(h, error, expected):
    h.api.returns["login"] = error
    await start_login(h)
    assert await h.send("pwd") == [expected]
    assert h.db.get_session(CHAT_ID) is None


async def test_bot_does_not_block_logins_itself(h):
    # Блокировку ведёт сервер; бот пропускает к нему каждую попытку.
    h.api.returns["login"] = Unauthorized(401, {"attemptsLeft": 1})
    for _ in range(6):
        await start_login(h)
        await h.send("wrong")
    assert len(h.api.called("login")) == 6
    h.api.returns["login"] = LoginResult("tok", EXPIRES)
    await start_login(h)
    assert await h.send("right") == [CHEF_WELCOME]


async def test_login_api_down(h):
    h.api.returns["login"] = ApiUnavailable(reason="ConnectError")
    await start_login(h)
    assert await h.send("pwd") == ["Сервер «Остатка» сейчас недоступен. Попробуйте позже."]


async def test_command_on_password_step_is_not_sent_as_password(h):
    await start_login(h)
    assert await h.send("/stock") == ["Сейчас идёт вход. Введите пароль или прервите вход: /cancel"]
    assert len(h.deleted()) == 1
    assert h.api.called("login") == []


async def test_cancel_during_login(h):
    await h.send("/login")
    assert await h.send("/cancel") == ["Действие отменено."]
    assert await h.send("admin") == ["Войдите через /login"]


async def test_logout_removes_token_and_notifications(h):
    h.login()
    assert CHAT_ID in h.db.active_chats(datetime.now(timezone.utc))
    assert await h.send("/logout") == ["Вы вышли. Уведомления в этот чат больше не приходят."]
    assert h.db.get_session(CHAT_ID) is None
    assert CHAT_ID not in h.db.active_chats(datetime.now(timezone.utc))


async def test_login_stores_role_and_restaurant_and_sets_role_menu(h):
    h.api.returns["login"] = LoginResult(jwt(), EXPIRES)
    h.api.returns["get_me"] = {
        "login": "waiter", "title": "Официант", "role": "Waiter", "sections": ["orders", "menu"],
        "shift": {"start": "12:00", "end": "24:00"}, "places": ["Стол 1"], "restaurant": "Демо-ресторан",
    }
    await start_login(h, "waiter")
    replies = await h.send("demo2026")
    assert replies == [
        "Вы вошли: waiter, Официант, Демо-ресторан.\n"
        "Доступно: /stoplist, /order, /close, /expiring, /stock, /dishes, /products, /hot, /journal"
    ]
    session = h.db.get_session(CHAT_ID)
    assert (session.role, session.sections, session.restaurant_id) == ("Waiter", ("orders", "menu"), RESTAURANT)
    menu = [r for r in h.session.requests if isinstance(r, SetMyCommands)][-1]
    assert menu.scope.chat_id == CHAT_ID
    assert [c.command for c in menu.commands] == [
        "stoplist", "order", "close", "expiring", "stock", "dishes", "products", "hot", "journal", "cancel", "logout"
    ]


async def test_start_shows_commands_of_the_role(h):
    h.login(role="Storekeeper")
    text = (await h.send("/start"))[0]
    assert "/supply" in text and "/buy" in text
    assert "/order" not in text and "/losses" not in text and "/preps" not in text


async def test_logout_and_revoked_token_reset_the_menu(h):
    h.login()
    await h.send("/logout")
    assert any(isinstance(r, DeleteMyCommands) and r.scope.chat_id == CHAT_ID for r in h.session.requests)

    h.login()
    h.session.requests.clear()
    h.api.returns["get_stop_list"] = Unauthorized(401)  # сотрудника удалили или сменили роль
    assert await h.send("/stoplist") == [SESSION_EXPIRED]
    assert any(isinstance(r, DeleteMyCommands) for r in h.session.requests)
    assert len(h.api.called("get_stop_list")) == 1  # на 401 запрос не повторяется
