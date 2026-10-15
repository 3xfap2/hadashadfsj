"""Поставка извне: частичная приёмка в боте. Просрочка «под списание»: напоминания только управляющему."""

from datetime import timedelta

import httpx

from ostatok_bot.monitor import REMIND_REPEAT
from ostatok_bot.storage import Storage

from .conftest import NOW, PRODUCTS, SALMON, FakeApi, batch, stop_item
from .test_incoming_and_alerts import client
from .test_monitor import REST_A, FakeBot, login, make_monitor

# Частичная приёмка

WAITING = {
    "id": "in-1", "number": "EDI-77", "supplier": "Фермер Иванов", "supplierInn": "7701234567",
    "receivedAt": "2026-10-09T09:30:00+00:00", "total": 1140, "status": "Pending",
    "items": [
        {"id": 11, "productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "quantity": 3, "shelfLifeHours": 72, "accepted": None},
        {"id": 12, "productId": "p-salmon", "productName": "Лосось охлаждённый", "unit": "Kg", "quantity": 2, "shelfLifeHours": 48, "accepted": None},
    ],
}


def keeper(h):
    h.login("tok", role="Storekeeper")
    h.api.returns["list_products"] = PRODUCTS
    h.api.returns["list_incoming"] = [WAITING]
    return h


async def test_incoming_shows_inn_and_partial_button(h):
    replies = await keeper(h).send("/incoming")
    assert replies[1].startswith("Поставка EDI-77, поставщик Фермер Иванов, ИНН 7701234567")
    assert h.buttons() == ["Принять", "Отклонить", "Принять частично"]


async def test_partial_accept_sends_amounts_per_line(h):
    await keeper(h).send("/incoming")
    question = await h.press("Принять частично")
    assert "1. Сливки 33% — заказано 3 л" in question[0] and "2. Лосось охлаждённый — заказано 2 кг" in question[0]

    # Неверный ответ: не столько чисел, больше заказанного, всё по нулям — переспрашивает, ничего не отправляя.
    assert (await h.send("2"))[0].startswith("Нужно 2 чисел")
    assert (await h.send("4 0"))[0].startswith("Сливки 33%: принять можно от 0 до 3 л")
    assert (await h.send("0 0"))[0].startswith("Ничего не принято")
    assert h.api.called("accept_incoming") == []

    h.api.returns["accept_incoming"] = {"id": "in-1", "number": "EDI-77", "items": []}
    replies = await h.send("2,5 0")
    assert h.api.called("accept_incoming") == [("tok", "in-1", [{"id": 11, "accepted": 2.5}, {"id": 12, "accepted": 0}])]
    assert replies == [
        "Поставка EDI-77 принята частично. На склад встало только принятое:\n"
        "Сливки 33% — принято 2,5 л из 3 л\n"
        "Лосось охлаждённый — принято 0 кг из 2 кг"
    ]


async def test_accept_with_amounts_posts_items():
    api, seen = client([httpx.Response(200, json={"id": "s1"}), httpx.Response(200, json={"id": "s1"})])
    await api.accept_incoming("t", "s1", [{"id": 1, "accepted": 0}])
    await api.accept_incoming("t", "s1")
    assert b'"items":[{"id":1,"accepted":0}]' in seen[0].content.replace(b" ", b"")
    assert seen[1].content in (b"", b"null")


# Напоминания управляющему


async def test_only_manager_gets_reminders_about_batches_under_write_off():
    db = Storage(":memory:")
    for chat, role in enumerate(("Manager", "Storekeeper", "Chef", "Waiter"), 1):
        login(db, chat, role, REST_A, hours=48)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = [batch("b1", "Fresh", 30), batch("b2", "Fresh", 40, product=SALMON, value=500.0)]
    clock = [NOW]
    monitor = make_monitor(api, db, bot, clock=lambda: clock[0])
    await monitor.poll_once()                     # первый опрос только запоминает
    assert bot.sent == []

    # b1 просрочена, b2 истекает через 4 часа.
    api.returns["get_batches"] = [batch("b1", "Expired", -1), batch("b2", "ExpiringSoon", 4, product=SALMON, value=500.0)]
    await monitor.poll_once()

    text, markup = bot.to(1)[0]
    assert "Напоминание управляющему." in text
    assert "Под списание — 1 партия на 342 ₽" in text and "• Сливки 33%, 0,9 л, поставка П-0413" in text
    assert "Склад → партия «Под списание» → «Списать»" in text
    assert "Истекают в ближайшие 6 ч:\n• Лосось охлаждённый, 0,9 кг, осталось 4 ч" in text
    assert text.count("Сливки 33%") == 1          # не дублируется строкой об изменении
    assert [row[0].callback_data for row in markup.inline_keyboard] == ["wo:b1"]
    for other in (2, 3, 4):
        assert all("Напоминание" not in t for t, _ in bot.to(other))
    assert "Под списание: Сливки 33%" in bot.to(2)[0][0]   # кладовщик видит смену статуса, но без напоминания

    # Повтор: не раньше чем через REMIND_REPEAT и только про несписанную; «скоро» — один раз.
    clock[0] = NOW + timedelta(minutes=1)
    await monitor.poll_once()
    assert len(bot.to(1)) == 1
    clock[0] = NOW + REMIND_REPEAT + timedelta(minutes=1)
    api.returns["get_batches"] = [batch("b1", "Expired", -5), batch("b2", "ExpiringSoon", 1, product=SALMON, value=500.0)]
    await monitor.poll_once()
    assert len(bot.to(1)) == 2
    repeat = bot.to(1)[1][0]
    assert "Под списание — 1 партия" in repeat and "Истекают" not in repeat
    assert all("Напоминание" not in t for t, _ in bot.to(2))

    # Списали — больше не напоминает.
    clock[0] = NOW + 3 * REMIND_REPEAT
    api.returns["get_batches"] = [batch("b2", "ExpiringSoon", 1, product=SALMON, value=500.0)]
    await monitor.poll_once()
    assert len(bot.to(1)) == 2


async def test_reminder_waits_for_manager_who_is_not_in_bot_yet():
    db = Storage(":memory:")
    login(db, 2, "Storekeeper", REST_A, hours=48)
    api, bot = FakeApi(), FakeBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = [batch("b1", "Fresh", 30), batch("b2", "Fresh", 40, product=SALMON, value=500.0)]
    clock = [NOW]
    monitor = make_monitor(api, db, bot, clock=lambda: clock[0])
    await monitor.poll_once()
    api.returns["get_batches"] = [batch("b1", "Expired", -1), batch("b2", "ExpiringSoon", 4, product=SALMON, value=500.0)]
    await monitor.poll_once()                     # управляющего в боте нет: кладовщик видит смену статуса, напоминать некому
    assert bot.to(2) and all("Напоминание" not in t for t, _ in bot.to(2))

    # Управляющий вошёл через минуту — получает и «под списание», и «истекает скоро», а не через REMIND_REPEAT.
    login(db, 1, "Manager", REST_A, hours=48)
    clock[0] = NOW + timedelta(minutes=1)
    await monitor.poll_once()
    text = bot.to(1)[0][0]
    assert "Под списание — 1 партия" in text and "Истекают в ближайшие 6 ч:\n• Лосось охлаждённый" in text
    clock[0] = NOW + timedelta(minutes=2)
    await monitor.poll_once()
    assert len(bot.to(1)) == 1                    # дошло — больше не повторяет


class ManagerUnreachableBot(FakeBot):
    """Telegram не принимает сообщения управляющему (чат 1), остальным доставляет."""

    def __init__(self):
        super().__init__()
        self.manager_down = True

    async def send_message(self, chat_id, text, reply_markup=None):
        if chat_id == 1 and self.manager_down:
            raise RuntimeError("Telegram недоступен")
        await super().send_message(chat_id, text, reply_markup)


async def test_reminder_is_repeated_when_message_to_manager_failed_but_reached_others():
    db = Storage(":memory:")
    login(db, 1, "Manager", REST_A, hours=48)
    login(db, 2, "Storekeeper", REST_A, hours=48)
    api, bot = FakeApi(), ManagerUnreachableBot()
    api.returns["get_stop_list"] = [stop_item(status="Available", portions=5)]
    api.returns["get_batches"] = [batch("b1", "Fresh", 30), batch("b2", "Fresh", 40, product=SALMON, value=500.0)]
    clock = [NOW]
    monitor = make_monitor(api, db, bot, clock=lambda: clock[0])
    await monitor.poll_once()
    api.returns["get_batches"] = [batch("b1", "Expired", -1), batch("b2", "ExpiringSoon", 4, product=SALMON, value=500.0)]
    await monitor.poll_once()
    assert bot.to(1) == [] and bot.to(2)          # кладовщику дошло, управляющему — нет

    bot.manager_down = False
    clock[0] = NOW + timedelta(minutes=1)
    await monitor.poll_once()
    text = bot.to(1)[0][0]
    assert "Под списание — 1 партия" in text and "Истекают в ближайшие 6 ч" in text
