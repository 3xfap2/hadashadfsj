from datetime import timedelta

from ostatok_bot.api import ApiUnavailable, Unauthorized
from ostatok_bot.monitor import Monitor, diff, render, snapshot
from ostatok_bot.storage import Storage

from ostatok_bot.texts import SESSION_EXPIRED

from .conftest import CARBONARA, NOW, ROLE_SECTIONS, SALMON, FakeApi, batch, stop_item


def state(stop_list, batches):
    return snapshot(stop_list, batches)


def test_first_run_sends_nothing():
    assert diff(None, [stop_item()], [batch(status="Expired")]) == []


def test_no_changes_sends_nothing():
    stop_list = [stop_item(), stop_item(CARBONARA, status="Low", portions=2)]
    batches = [batch("b1", "Expired"), batch("b2", "ExpiringSoon", 5)]
    assert diff(state(stop_list, batches), stop_list, batches) == []


def test_dish_goes_to_stop_with_reason():
    before = state([stop_item(status="Low", portions=1)], [])
    events = diff(before, [stop_item(status="Stop", reason="OutOfStock")], [])
    assert [(e.kind, e.text) for e in events] == [
        ("dish_stop", "В стопе: Тартар из лосося — закончился продукт «Лосось охлаждённый»")
    ]


def test_dish_leaves_stop():
    before = state([stop_item(status="Stop")], [])
    events = diff(before, [stop_item(status="Available", portions=8)], [])
    assert [(e.kind, e.text) for e in events] == [("dish_resumed", "Снова доступно: Тартар из лосося")]


def test_batch_becomes_expiring_soon():
    before = state([], [batch("b1", "Fresh", 30)])
    events = diff(before, [], [batch("b1", "ExpiringSoon", 5)])
    assert [(e.kind, e.text) for e in events] == [
        ("batch_expiring", "Скоро истекает: Сливки 33%, 0,9 л, поставка П-0413, осталось 5 ч")
    ]


def test_batch_becomes_expired_with_write_off_button():
    before = state([], [batch("b1", "ExpiringSoon", 1)])
    events = diff(before, [], [batch("b1", "Expired", -1)])
    assert [(e.kind, e.text) for e in events] == [("batch_expired", "Просрочено: Сливки 33%, 0,9 л, поставка П-0413")]
    text, keyboard = render(events)
    assert keyboard.inline_keyboard[0][0].text == "Списать"
    assert keyboard.inline_keyboard[0][0].callback_data == "wo:b1"


def test_status_changes_that_are_not_events():
    # Available -> Low, исчезнувшая партия, свежая новая партия, Stop с другой причиной
    before = state([stop_item(status="Available", portions=9), stop_item(CARBONARA, reason="Expired")],
                   [batch("b1", "Expired")])
    now_stop = [stop_item(status="Low", portions=2), stop_item(CARBONARA, reason="OutOfStock")]
    assert diff(before, now_stop, [batch("b2", "Fresh", 40)]) == []


def test_several_events_render_as_one_message():
    before = state([stop_item(status="Available", portions=5), stop_item(CARBONARA, status="Stop")],
                   [batch("b1", "Fresh", 30), batch("b2", "ExpiringSoon", 2, product=SALMON)])
    events = diff(
        before,
        [stop_item(status="Stop"), stop_item(CARBONARA, status="Available", portions=4)],
        [batch("b1", "ExpiringSoon", 6), batch("b2", "Expired", -1, product=SALMON)],
    )
    assert [e.kind for e in events] == ["dish_stop", "dish_resumed", "batch_expiring", "batch_expired"]
    text, keyboard = render(events)
    assert text.count("\n") == 3
    assert [row[0].callback_data for row in keyboard.inline_keyboard] == ["wo:b2"]


# Опрос целиком

REST_A = "rest-a"
REST_B = "rest-b"


def test_manual_stop_reason_text():
    before = state([stop_item(status="Available", portions=8)], [])
    events = diff(before, [stop_item(status="Stop", reason="Manual")], [])
    assert events[0].text == "В стопе: Тартар из лосося — снято с продажи вручную"


class FakeBot:
    def __init__(self):
        self.sent = []
        self.commands_reset = []

    async def send_message(self, chat_id, text, reply_markup=None):
        self.sent.append((chat_id, text, reply_markup))

    async def delete_my_commands(self, scope=None):
        self.commands_reset.append(scope.chat_id)

    def to(self, chat_id):
        return [(text, markup) for cid, text, markup in self.sent if cid == chat_id]


def login(db, chat_id, role, restaurant, token=None, hours=1):
    db.save_session(
        chat_id, token or f"token-{chat_id}", NOW + timedelta(hours=hours),
        role=role, sections=ROLE_SECTIONS[role], restaurant_id=restaurant,
    )


def by_token(responses):
    """Ответ API зависит от токена: у каждого ресторана свои данные."""
    def answer(token):
        value = responses[token]
        if isinstance(value, BaseException):
            raise value
        return value
    return answer


def make_monitor(api, db, bot, clock=lambda: NOW):
    return Monitor(api, db, bot, 30, clock=clock)


async def test_restaurants_are_polled_separately_and_never_mixed(tmp_path):
    db = Storage(tmp_path / "bot.sqlite3")
    login(db, 1, "Chef", REST_A, "token-a")
    login(db, 2, "Chef", REST_B, "token-b")
    api, bot = FakeApi(), FakeBot()
    stop_a = {"token-a": [stop_item(status="Available", portions=5)], "token-b": [stop_item(CARBONARA, status="Low", portions=2)]}
    batches = {"token-a": [], "token-b": []}
    api.returns["get_stop_list"] = by_token(stop_a)
    api.returns["get_batches"] = by_token(batches)
    monitor = make_monitor(api, db, bot)

    assert await monitor.poll_once() == {REST_A: [], REST_B: []}   # первый опрос только запоминает
    assert bot.sent == []

    stop_a["token-a"] = [stop_item(status="Stop", reason="OutOfStock")]   # меняется только ресторан A
    events = await monitor.poll_once()
    assert len(events[REST_A]) == 1 and events[REST_B] == []
    assert [cid for cid, _, _ in bot.sent] == [1]               # ресторан B ничего не получил

    assert await monitor.poll_once() == {REST_A: [], REST_B: []}  # повтор без изменений
    assert len(bot.sent) == 1

    db.close()
    restarted = make_monitor(api, Storage(tmp_path / "bot.sqlite3"), bot)  # перезапуск бота
    assert await restarted.poll_once() == {REST_A: [], REST_B: []}
    assert len(bot.sent) == 1
    assert {args[0] for args in api.called("get_batches")} == {"token-a", "token-b"}


async def test_roles_see_what_their_section_allows():
    db = Storage(":memory:")
    login(db, 1, "Waiter", REST_A)
    login(db, 2, "Storekeeper", REST_A)
    login(db, 3, "Cook", REST_A)
    login(db, 4, "Manager", REST_A)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = [batch("b1", "ExpiringSoon", 2)]
    monitor = make_monitor(api, db, bot)
    await monitor.poll_once()

    api.returns["get_stop_list"] = [stop_item(status="Stop")]
    api.returns["get_batches"] = [batch("b1", "Expired", -1)]
    await monitor.poll_once()

    waiter = bot.to(1)
    assert waiter == [("В стопе: Тартар из лосося — просрочен продукт «Лосось охлаждённый»", None)]
    keeper_text, keeper_markup = bot.to(2)[0]
    assert "Просрочено: Сливки 33%" in keeper_text and keeper_markup.inline_keyboard[0][0].text == "Списать"
    cook_text, cook_markup = bot.to(3)[0]
    assert "Просрочено: Сливки 33%" in cook_text and cook_markup is None   # видит партии, но не списывает
    assert bot.to(4)[0][1] is not None


async def test_revoked_token_logs_that_chat_out_and_uses_a_colleague():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "revoked")
    login(db, 2, "Waiter", REST_A, "good")
    api, bot = FakeApi(), FakeBot()
    stop = {"revoked": Unauthorized(401), "good": [stop_item(status="Available", portions=5)]}
    api.returns["get_stop_list"] = by_token(stop)
    api.returns["get_batches"] = []
    monitor = make_monitor(api, db, bot)

    await monitor.poll_once()
    assert db.get_session(1) is None
    assert bot.to(1) == [(SESSION_EXPIRED + "\nПока вы не войдёте, уведомления не приходят.", None)]
    assert bot.commands_reset == [1]
    assert [a[0] for a in api.called("get_stop_list")] == ["revoked", "good"]

    stop["good"] = [stop_item(status="Stop")]
    await monitor.poll_once()
    assert len(bot.to(2)) == 1 and len(bot.to(1)) == 1   # вышедший чат больше ничего не получает


async def test_all_tokens_revoked_sends_no_events():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, "x")
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = Unauthorized(401)
    assert await make_monitor(api, db, bot).poll_once() == {REST_A: []}
    assert db.load_state("restaurant:" + REST_A) is None


async def test_api_unavailable_sends_nothing_and_keeps_state():
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = []
    monitor = make_monitor(api, db, bot)
    await monitor.poll_once()
    saved = db.load_state("restaurant:" + REST_A)

    api.returns["get_stop_list"] = ApiUnavailable(reason="ConnectError")
    assert await monitor.poll_once() == {REST_A: []}
    assert bot.sent == []
    assert db.load_state("restaurant:" + REST_A) == saved
    assert db.get_session(1) is not None


async def test_stale_state_counts_as_first_run():
    # Ресторан долго не опрашивали (никто не был в боте): накопившиеся изменения не рассылаются пачкой.
    db = Storage(":memory:")
    login(db, 1, "Chef", REST_A, hours=48)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = []
    await make_monitor(api, db, bot).poll_once()

    api.returns["get_stop_list"] = [stop_item(status="Stop")]
    later = NOW + timedelta(hours=3)
    assert await make_monitor(api, db, bot, clock=lambda: later).poll_once() == {REST_A: []}
    assert bot.sent == []


async def test_chats_without_restaurant_are_skipped():
    db = Storage(":memory:")
    db.save_session(1, "t", NOW + timedelta(hours=1))   # сессия без ресторана: старый вход
    api, bot = FakeApi(), FakeBot()
    assert await make_monitor(api, db, bot).poll_once() == {}
    assert api.calls == []
