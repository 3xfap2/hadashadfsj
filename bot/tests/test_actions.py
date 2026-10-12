"""Диалоги действий целиком: от команды до запроса к API и ответа на каждый код."""

import uuid

import pytest

from ostatok_bot.api import ApiUnavailable, Conflict, NotFound, ValidationFailed

from .conftest import CARBONARA, CREAM, DISHES, ME, PLACES, PRODUCTS, SALMON, TARTARE, batch, stop_item

pytestmark = pytest.mark.asyncio

ERR_400 = ValidationFailed(400, {"errors": {"quantity": ["Количество должно быть больше нуля"]}})


@pytest.fixture
def logged(h):
    h.login("tok")
    h.api.returns["list_products"] = PRODUCTS
    h.api.returns["list_dishes"] = DISHES
    h.api.returns["get_me"] = ME
    return h


# Списание


async def open_write_off(h):
    h.api.returns["get_batches"] = [batch("b1", "Expired", -3)]
    assert await h.click("wo:b1") == ["Списать 0,9 л на 342 ₽: Сливки 33% (поставка П-0413)?"]
    assert h.buttons() == ["Списать", "Отмена"]


@pytest.mark.parametrize(
    "result, expected",
    [
        (None, "Списано: Сливки 33%, 0,9 л."),
        (NotFound(404), "Партия не найдена. Возможно, её уже списали."),
        (Conflict(409), "В партии уже ничего нет: Сливки 33%, поставка П-0413."),
        (ValidationFailed(400, {"errors": {"id": ["Неверный идентификатор"]}}), "Неверный идентификатор"),
    ],
)
async def test_write_off(logged, result, expected):
    await open_write_off(logged)
    logged.api.returns["write_off_batch"] = result
    assert await logged.press("Списать") == [expected]
    assert logged.api.called("write_off_batch") == [("tok", "b1")]


async def test_write_off_cancel_and_missing_batch(logged):
    await open_write_off(logged)
    assert await logged.press("Отмена") == ["Действие отменено."]
    assert logged.api.called("write_off_batch") == []

    logged.api.returns["get_batches"] = []
    assert await logged.click("wo:b1") == ["Партия уже списана или израсходована."]


async def test_expiring_shows_write_off_only_for_expired(logged):
    logged.api.returns["get_batches"] = [batch("b1", "Expired", -3), batch("b2", "ExpiringSoon", 5, product=SALMON)]
    await logged.send("/expiring")
    assert logged.buttons() == ["Списать"]
    assert logged.button_data("Списать") == "wo:b1"


# Поставка


async def fill_supply(h):
    assert (await h.send("/supply"))[0].startswith("Новая поставка")
    assert await h.send("П-0413") == ["Поставщик? Его можно не указывать."]
    assert await h.send("ООО Рыба") == ["Выберите продукт:"]
    assert h.buttons()[:3] == ["Лосось охлаждённый (кг)", "Сливки 33% (л)", "Яйцо (шт)"]
    assert await h.press("Сливки 33% (л)") == ["Сливки 33%. Сколько пришло, л?"]
    assert await h.send("ноль") == ["«ноль» не похоже на число. Пример: 2,5. Сколько пришло, л?"]
    assert await h.send("0") == ["Число должно быть больше нуля. Сколько пришло, л?"]
    assert await h.send("2,5") == ["Срок хранения в часах? По нормативу — 72 ч."]
    assert await h.press("По нормативу (72 ч)") == [
        "Добавлено: Сливки 33% — 2,5 л, срок по нормативу (72 ч)\nПозиций в поставке: 1"
    ]
    await h.press("Добавить ещё продукт")
    await h.press("Лосось охлаждённый (кг)")
    await h.send("1.2")
    assert await h.send("1,5") == ["Нужно целое число, без дробной части. Введите срок хранения в часах или нажмите «По нормативу»."]
    await h.send("24")
    assert await h.press("Готово") == [
        "Поставка П-0413, поставщик ООО Рыба:\n"
        "1. Сливки 33% — 2,5 л, срок по нормативу (72 ч)\n"
        "2. Лосось охлаждённый — 1,2 кг, срок 24 ч\n\n"
        "Принять поставку?"
    ]


SUPPLY_201 = {
    "id": "s1", "number": "П-0413", "createdAt": "2026-10-09T09:00:00+00:00",
    "items": [
        {"batchId": "b1", "productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "quantity": 2.5,
         "remaining": 2.5, "expiresAt": "2026-10-12T09:00:00+00:00"},
        {"batchId": "b2", "productId": "p-salmon", "productName": "Лосось охлаждённый", "unit": "Kg",
         "quantity": 1.2, "remaining": 1.2, "expiresAt": "2026-10-10T09:00:00+00:00"},
    ],
}


async def test_supply_201(logged):
    await fill_supply(logged)
    logged.api.returns["create_supply"] = SUPPLY_201
    assert await logged.press("Принять поставку") == [
        "Отправляю поставку…",
        "Поставка П-0413 принята. Партии:\n"
        "Сливки 33% — 2,5 л, годен до 12.10 12:00\n"
        "Лосось охлаждённый — 1,2 кг, годен до 10.10 12:00"
    ]
    token, number, items, supplier, supply_id = logged.api.called("create_supply")[0]
    assert (token, number, supplier) == ("tok", "П-0413", "ООО Рыба")
    assert uuid.UUID(supply_id)  # идентификатор попытки поставки
    assert items == [
        {"productId": "p-cream", "quantity": 2.5, "shelfLifeHours": None},
        {"productId": "p-salmon", "quantity": 1.2, "shelfLifeHours": 24},
    ]


async def test_supply_409_asks_new_number_and_keeps_items(logged):
    await fill_supply(logged)
    logged.api.returns["create_supply"] = Conflict(409)
    logged.api.returns["list_supplies"] = [{**SUPPLY_201, "id": "чужая"}]   # номер занят другой поставкой
    assert await logged.press("Принять поставку") == [
        "Отправляю поставку…", "Поставка с номером П-0413 уже есть. Введите другой номер."
    ]
    replies = await logged.send("П-0414")
    assert replies[0].startswith("Поставка П-0414, поставщик ООО Рыба:\n1. Сливки 33%")
    logged.api.returns["create_supply"] = {**SUPPLY_201, "number": "П-0414"}
    assert (await logged.press("Принять поставку"))[1].startswith("Поставка П-0414 принята")
    assert len(logged.api.called("create_supply")) == 2


async def test_supply_400_and_404(logged):
    await fill_supply(logged)
    logged.api.returns["create_supply"] = ERR_400
    assert await logged.press("Принять поставку") == [
        "Отправляю поставку…", "Поставка не принята.\nКоличество должно быть больше нуля\nНачать заново: /supply"
    ]
    await fill_supply(logged)
    logged.api.returns["create_supply"] = NotFound(404)
    assert await logged.press("Принять поставку") == [
        "Отправляю поставку…",
        "Поставка не принята.\nКакой-то продукт не найден, возможно его удалили.\nНачать заново: /supply",
    ]


async def test_supply_cancel_on_any_step(logged):
    await logged.send("/supply")
    await logged.send("П-1")
    await logged.press("Без поставщика")
    await logged.press("Сливки 33% (л)")
    assert await logged.send("/cancel") == ["Действие отменено."]
    assert await logged.send("2") == ["Не понял. Список команд: /start"]
    assert logged.api.called("create_supply") == []


async def test_supply_api_down_on_send(logged):
    await fill_supply(logged)
    logged.api.returns["create_supply"] = ApiUnavailable(reason="ConnectError")   # запрос не дошёл
    assert await logged.press("Принять поставку") == [
        "Отправляю поставку…", "Сервер недоступен, поставка не отправлена. Попробуйте ещё раз."
    ]
    logged.api.returns["create_supply"] = SUPPLY_201
    assert (await logged.press("Принять поставку"))[1].startswith("Поставка П-0413 принята")
    first, second = logged.api.called("create_supply")
    assert first[4] == second[4]


async def test_supply_lost_answer_is_not_accepted_twice(logged):
    # Сервер принял поставку, но ответ потерялся. Повтор с тем же id даёт 409, и бот видит, что поставка наша.
    await fill_supply(logged)
    logged.api.returns["create_supply"] = ApiUnavailable(reason="ReadTimeout", unsure=True)
    replies = await logged.press("Принять поставку")
    assert replies[1].startswith("Сервер не ответил, поставка могла пройти.")
    assert logged.buttons() == ["Проверить и принять", "Отмена"]
    supply_id = logged.api.called("create_supply")[0][4]
    logged.api.returns["create_supply"] = Conflict(409)
    logged.api.returns["list_supplies"] = [{**SUPPLY_201, "id": supply_id}]
    replies = await logged.press("Проверить и принять")
    assert replies[1].startswith("Поставка уже была принята при прошлой попытке.\n\nПоставка П-0413 принята.")
    assert await logged.send("П-0414") == ["Не понял. Список команд: /start"]   # нового номера не просят


async def test_supply_server_error_from_app_is_not_retried(logged):
    await fill_supply(logged)
    logged.api.returns["create_supply"] = ApiUnavailable(500, {"detail": "Ошибка базы."}, from_app=True)
    assert await logged.press("Принять поставку") == [
        "Отправляю поставку…", "Поставка не принята.\nОшибка базы.\nНачать заново: /supply"
    ]


# Заказ


async def fill_order(h, place="Стол 7"):
    h.api.returns["get_stop_list"] = [
        stop_item(TARTARE, status="Stop", reason="Expired"),
        stop_item(CARBONARA, status="Low", portions=3, product=CREAM),
    ]
    await h.send("/order")
    assert h.buttons()[:2] == ["Карбонара — осталось 3 порц.", "Тартар из лосося — стоп"]
    assert await h.press("Тартар из лосося — стоп") == [
        "Блюдо в стопе: просрочен продукт «Лосось охлаждённый». Выбрать его нельзя."
    ]
    assert await h.press("Карбонара — осталось 3 порц.") == ["Карбонара. Сколько порций? Доступно примерно 3."]
    assert await h.send("1,5") == ["Нужно целое число, без дробной части. Сколько порций?"]
    assert await h.send("2") == ["В заказе:\nКарбонара × 2 — 1 080 ₽"]
    await h.press("Добавить ещё блюдо")
    await h.press("Карбонара — осталось 3 порц.")
    assert await h.send("1") == ["В заказе:\nКарбонара × 3 — 1 620 ₽"]
    assert await h.press("Готово") == ["Куда заказ?"]
    assert h.buttons() == PLACES + ["Без места", "Отмена"]
    if place:
        assert await h.press(place) == [
            f"Заказ:\nКарбонара × 3 — 1 620 ₽\nМесто: {place}\nИтого: 1 620 ₽\n\nСоздать заказ?"
        ]
    else:
        assert await h.press("Без места") == ["Заказ:\nКарбонара × 3 — 1 620 ₽\nИтого: 1 620 ₽\n\nСоздать заказ?"]


ACCEPTED = {
    "id": "o1", "number": 41, "createdAt": "2026-10-09T09:00:00+00:00", "status": "Accepted",
    "place": "Стол 7", "total": 1620,
    "items": [{"dishId": CARBONARA["id"], "dishName": "Карбонара", "quantity": 3}], "shortages": [],
    "writeOffs": [
        {"productName": "Сливки 33%", "unit": "L", "supplyNumber": "П-0410", "quantity": 0.1},
        {"productName": "Сливки 33%", "unit": "L", "supplyNumber": "П-0413", "quantity": 0.05},
        {"productName": "Яйцо", "unit": "Pcs", "supplyNumber": "П-0411", "quantity": 3},
    ],
}


async def test_order_201_shows_number_place_and_write_offs(logged):
    await fill_order(logged)
    logged.api.returns["create_order"] = ACCEPTED
    assert await logged.press("Создать заказ") == ["Отправляю заказ…", 
        "Заказ №41 принят, Стол 7.\nКарбонара × 3\nСумма: 1 620 ₽\n\n"
        "Списано:\n"
        "Сливки 33% — 0,1 л, поставка П-0410\n"
        "Сливки 33% — 0,05 л, поставка П-0413\n"
        "Яйцо — 3 шт, поставка П-0411"
    ]
    (call,) = logged.api.called("create_order")
    assert call[:3] == ("tok", [{"dishId": "d-carbonara", "quantity": 3}], "Стол 7")
    assert uuid.UUID(call[3])  # идентификатор попытки заказа


async def test_order_without_place(logged):
    await fill_order(logged, place=None)
    logged.api.returns["create_order"] = {**ACCEPTED, "place": "", "writeOffs": []}
    assert await logged.press("Создать заказ") == ["Отправляю заказ…", "Заказ №41 принят.\nКарбонара × 3\nСумма: 1 620 ₽"]
    assert logged.api.called("create_order")[0][2] is None


async def test_order_place_step_skipped_when_restaurant_has_no_places(logged):
    logged.api.returns["get_me"] = {**ME, "places": []}
    logged.api.returns["get_stop_list"] = []
    await logged.send("/order")
    await logged.press("Карбонара — 540 ₽")
    await logged.send("1")
    assert await logged.press("Готово") == ["Заказ:\nКарбонара × 1 — 540 ₽\nИтого: 540 ₽\n\nСоздать заказ?"]


async def test_order_portions_limit(logged):
    logged.api.returns["get_stop_list"] = []
    await logged.send("/order")
    await logged.press("Карбонара — 540 ₽")
    assert await logged.send("1001") == ["Не больше 1000 порций одного блюда. Сколько порций?"]
    await logged.send("999")
    await logged.press("Добавить ещё блюдо")
    await logged.press("Карбонара — 540 ₽")
    assert await logged.send("2") == ["Не больше 1000 порций одного блюда. В заказе уже 999. Сколько порций?"]
    assert (await logged.send("1"))[0] == "В заказе:\nКарбонара × 1000 — 540 000 ₽"


async def test_order_409_rejected_shows_shortages(logged):
    await fill_order(logged)
    logged.api.returns["create_order"] = {
        **ACCEPTED, "id": "o2", "number": 42, "status": "Rejected", "writeOffs": [],
        "shortages": [{"productId": "p-cream", "productName": "Сливки 33%", "unit": "L",
                       "required": 0.15, "available": 0.1}],
    }
    assert await logged.press("Создать заказ") == ["Отправляю заказ…", 
        "Заказ №42 отклонён, не хватает продуктов:\nСливки 33%: нужно 0,15 л, есть 0,1 л"
    ]


async def test_order_400_and_404(logged):
    await fill_order(logged)
    logged.api.returns["create_order"] = ValidationFailed(400, {"errors": {"place": ["Такого места в ресторане нет."]}})
    assert await logged.press("Создать заказ") == ["Отправляю заказ…", 
        "Заказ не создан.\nТакого места в ресторане нет.\nНачать заново: /order"
    ]
    await fill_order(logged)
    logged.api.returns["create_order"] = NotFound(404)
    assert await logged.press("Создать заказ") == ["Отправляю заказ…", 
        "Заказ не создан.\nКакое-то блюдо не найдено, возможно его удалили.\nНачать заново: /order"
    ]


# Продукты


async def test_products_list(logged):
    replies = await logged.send("/products")
    assert replies == [
        "Продукты:\n"
        "Лосось охлаждённый — кг, хранение 48 ч, 1 900 ₽ за кг\n"
        "Сливки 33% — л, хранение 72 ч, 380 ₽ за л\n"
        "Яйцо — шт, хранение 240 ч, 12 ₽ за шт"
    ]
    assert logged.buttons() == ["Добавить", "Изменить", "Удалить"]


async def fill_new_product(h, name="Сыр пармезан"):
    await h.send("/products")
    await h.press("Добавить")
    await h.send(name)
    assert h.buttons() == ["кг", "л", "шт", "Отмена"]
    assert await h.send("кг") == ["Выберите единицу кнопкой: кг, л или шт."]
    await h.press("кг")
    assert await h.send("-1") == ["Число должно быть больше нуля. Срок хранения в часах?"]
    await h.send("720")
    assert await h.send("2400,5") == ["Новый продукт:\nСыр пармезан — кг, хранение 720 ч, 2 401 ₽ за кг\n\nСохранить?"]


async def test_product_create_201(logged):
    await fill_new_product(logged)
    saved = {"id": "p-new", "name": "Сыр пармезан", "unit": "Kg", "shelfLifeHours": 720, "cost": 2400.5}
    logged.api.returns["create_product"] = saved
    assert await logged.press("Сохранить") == ["Продукт добавлен: Сыр пармезан — кг, хранение 720 ч, 2 401 ₽ за кг"]
    assert logged.api.called("create_product") == [("tok", "Сыр пармезан", "Kg", 720, 2400.5)]


async def test_product_create_409_then_new_name(logged):
    await fill_new_product(logged)
    logged.api.returns["create_product"] = Conflict(409)
    assert await logged.press("Сохранить") == ["Продукт «Сыр пармезан» уже есть. Введите другое название."]
    replies = await logged.send("Сыр пармезан 24 мес")
    assert replies == ["Новый продукт:\nСыр пармезан 24 мес — кг, хранение 720 ч, 2 401 ₽ за кг\n\nСохранить?"]


async def test_product_create_400(logged):
    await fill_new_product(logged)
    logged.api.returns["create_product"] = ValidationFailed(400, {"errors": {"name": ["Слишком длинное название"]}})
    assert await logged.press("Сохранить") == ["Не сохранено.\nСлишком длинное название\nПопробуйте снова: /products"]


async def fill_edit_product(h):
    await h.send("/products")
    await h.press("Изменить")
    replies = await h.press("Сливки 33% (л)")
    assert replies == ["Название продукта?\nСейчас: «Сливки 33%»."]
    assert h.buttons() == ["Оставить", "Отмена"]
    await h.press("Оставить")
    assert h.buttons() == ["кг", "л", "шт", "Оставить", "Отмена"]
    await h.press("Оставить")
    await h.send("96")
    assert await h.press("Оставить") == [
        "Было: Сливки 33% — л, хранение 72 ч, 380 ₽ за л\nСтанет: Сливки 33% — л, хранение 96 ч, 380 ₽ за л\n\n"
        "Сохранить изменения?"
    ]


@pytest.mark.parametrize(
    "result, expected",
    [
        ({**CREAM, "shelfLifeHours": 96}, "Сохранено: Сливки 33% — л, хранение 96 ч, 380 ₽ за л"),
        (NotFound(404), "Продукт не найден, возможно его уже удалили. Список: /products"),
        (Conflict(409), "Продукт «Сливки 33%» уже есть. Введите другое название."),
        (ERR_400, "Не сохранено.\nКоличество должно быть больше нуля\nПопробуйте снова: /products"),
    ],
)
async def test_product_update(logged, result, expected):
    await fill_edit_product(logged)
    logged.api.returns["update_product"] = result
    assert await logged.press("Сохранить") == [expected]
    assert logged.api.called("update_product") == [("tok", "p-cream", "Сливки 33%", "L", 96, 380, None, None)]


@pytest.mark.parametrize(
    "result, expected",
    [
        (None, "Продукт «Яйцо» удалён."),
        (NotFound(404), "Продукт «Яйцо» не найден, возможно его уже удалили."),
        (Conflict(409), "Продукт «Яйцо» используется в поставках или техкартах, удалить нельзя."),
    ],
)
async def test_product_delete(logged, result, expected):
    await logged.send("/products")
    await logged.press("Удалить")
    assert await logged.press("Яйцо (шт)") == ["Удалить продукт «Яйцо»?"]
    logged.api.returns["delete_product"] = result
    assert await logged.press("Удалить") == [expected]
    assert logged.api.called("delete_product") == [("tok", "p-egg")]


async def test_product_pagination(logged):
    many = [{"id": f"p{i:02d}", "name": f"Продукт {i:02d}", "unit": "Kg", "shelfLifeHours": 1, "cost": 1}
            for i in range(19)]
    logged.api.returns["list_products"] = many
    await logged.send("/products")
    await logged.press("Изменить")
    assert logged.buttons() == [f"Продукт {i:02d} (кг)" for i in range(8)] + ["Дальше", "Отмена"]
    await logged.press("Дальше")
    assert logged.buttons() == [f"Продукт {i:02d} (кг)" for i in range(8, 16)] + ["Назад", "Дальше", "Отмена"]
    await logged.press("Дальше")
    assert logged.buttons() == [f"Продукт {i:02d} (кг)" for i in range(16, 19)] + ["Назад", "Отмена"]
    await logged.press("Назад")
    assert logged.buttons()[0] == "Продукт 08 (кг)"


# Блюда


async def test_dish_card(logged):
    await logged.send("/dishes")
    assert logged.buttons() == ["Карбонара — 540 ₽", "Тартар из лосося — 690 ₽", "Добавить"]
    assert await logged.press("Карбонара — 540 ₽") == [
        "Карбонара\nЦена: 540 ₽\nСостав:\n— Сливки 33%: 0,05 л\n— Яйцо: 1 шт"
    ]
    assert logged.buttons() == ["Изменить", "Удалить", "Снять с продажи", "К списку"]


async def fill_new_dish(h):
    await h.send("/dishes")
    await h.press("Добавить")
    await h.send("Омлет")
    assert await h.send("350") == ["Теперь состав. Выберите продукт:"]
    await h.press("Яйцо (шт)")
    assert await h.send("3") == ["Состав:\nЯйцо: 3 шт"]
    await h.press("Добавить ещё продукт")
    await h.press("Сливки 33% (л)")
    await h.send("0,03")
    await h.press("Добавить ещё продукт")
    assert await h.press("Яйцо (шт)") == ["Сколько «Яйцо» на порцию, шт? Сейчас: 3 шт."]
    assert await h.send("2") == ["Состав:\nЯйцо: 2 шт\nСливки 33%: 0,03 л"]
    assert await h.press("Готово") == [
        "Омлет\nЦена: 350 ₽\nСостав:\n— Яйцо: 2 шт\n— Сливки 33%: 0,03 л\n\nСохранить блюдо?"
    ]


async def test_dish_create_201(logged):
    await fill_new_dish(logged)
    logged.api.returns["create_dish"] = {
        "id": "d-new", "name": "Омлет", "price": 350,
        "recipe": [{"productId": "p-egg", "quantity": 2}, {"productId": "p-cream", "quantity": 0.03}],
    }
    replies = await logged.press("Сохранить")
    assert replies[0].startswith("Блюдо добавлено.\n\nОмлет\nЦена: 350 ₽")
    assert logged.api.called("create_dish") == [
        ("tok", "Омлет", 350, [{"productId": "p-egg", "quantity": 2}, {"productId": "p-cream", "quantity": 0.03}])
    ]


@pytest.mark.parametrize(
    "result, expected",
    [
        (Conflict(409), "Блюдо «Омлет» уже есть. Введите другое название."),
        (ERR_400, "Не сохранено.\nКоличество должно быть больше нуля\nПопробуйте снова: /dishes"),
        (NotFound(404), "Блюдо или продукт из состава не найдены, возможно их удалили. Список: /dishes"),
    ],
)
async def test_dish_create_errors(logged, result, expected):
    await fill_new_dish(logged)
    logged.api.returns["create_dish"] = result
    assert await logged.press("Сохранить") == [expected]


async def test_dish_edit_name_price_and_recipe(logged):
    await logged.send("/dishes")
    await logged.press("Карбонара — 540 ₽")
    await logged.press("Изменить")
    assert logged.buttons() == ["Название", "Цена", "Состав", "Сохранить", "Отмена"]

    await logged.press("Название")
    await logged.send("Карбонара классическая")
    await logged.press("Цена")
    assert await logged.send("бесплатно") == ["«бесплатно» не похоже на число. Пример: 2,5. Цена блюда, ₽?"]
    await logged.send("590")

    await logged.press("Состав")
    assert logged.buttons() == ["Сливки 33%: 0,05 л", "Яйцо: 1 шт", "Добавить продукт", "Готово"]
    await logged.press("Сливки 33%: 0,05 л")
    await logged.press("Изменить количество")
    await logged.send("0,06")
    await logged.press("Яйцо: 1 шт")
    await logged.press("Убрать")
    assert logged.buttons() == ["Сливки 33%: 0,06 л", "Добавить продукт", "Готово"]
    await logged.press("Добавить продукт")
    await logged.press("Лосось охлаждённый (кг)")
    await logged.send("0,1")
    await logged.press("Готово")
    replies = await logged.press("Сохранить")
    assert replies == [
        "Карбонара классическая\nЦена: 590 ₽\nСостав:\n— Сливки 33%: 0,06 л\n— Лосось охлаждённый: 0,1 кг\n\n"
        "Сохранить изменения?"
    ]
    logged.api.returns["update_dish"] = {
        "id": "d-carbonara", "name": "Карбонара классическая", "price": 590,
        "recipe": [{"productId": "p-cream", "quantity": 0.06}, {"productId": "p-salmon", "quantity": 0.1}],
    }
    assert (await logged.press("Сохранить"))[0].startswith("Изменения сохранены.")
    # состав отправляется целиком
    assert logged.api.called("update_dish") == [(
        "tok", "d-carbonara", "Карбонара классическая", 590,
        [{"productId": "p-cream", "quantity": 0.06}, {"productId": "p-salmon", "quantity": 0.1}],
        None, None,
    )]


async def test_dish_edit_keeps_photo(logged):
    await logged.send("/dishes")
    await logged.press("Тартар из лосося — 690 ₽")
    await logged.press("Изменить")
    await logged.press("Цена")
    await logged.send("720")
    await logged.press("Сохранить")
    logged.api.returns["update_dish"] = {**TARTARE, "price": 720}
    await logged.press("Сохранить")
    assert logged.api.called("update_dish") == [(
        "tok", "d-tartare", "Тартар из лосося", 720, TARTARE["recipe"], "/dishes/tartare.jpg", "Холодные закуски",
    )]


async def test_dish_edit_empty_recipe_is_blocked(logged):
    await logged.send("/dishes")
    await logged.press("Тартар из лосося — 690 ₽")
    await logged.press("Изменить")
    await logged.press("Состав")
    await logged.press("Лосось охлаждённый: 0,12 кг")
    await logged.press("Убрать")
    assert await logged.press("Готово") == ["В составе должен быть хотя бы один продукт."]


@pytest.mark.parametrize(
    "result, expected",
    [
        (NotFound(404), "Блюдо или продукт из состава не найдены, возможно их удалили. Список: /dishes"),
        (Conflict(409), "Блюдо «Тартар из лосося» уже есть. Введите другое название."),
    ],
)
async def test_dish_update_errors(logged, result, expected):
    await logged.send("/dishes")
    await logged.press("Тартар из лосося — 690 ₽")
    await logged.press("Изменить")
    await logged.press("Сохранить")
    logged.api.returns["update_dish"] = result
    assert await logged.press("Сохранить") == [expected]


@pytest.mark.parametrize(
    "result, expected",
    [
        (None, "Блюдо «Тартар из лосося» удалено."),
        (NotFound(404), "Блюдо «Тартар из лосося» не найдено, возможно его уже удалили."),
        (Conflict(409), "По блюду «Тартар из лосося» уже есть заказы, удалить нельзя."),
    ],
)
async def test_dish_delete(logged, result, expected):
    await logged.send("/dishes")
    await logged.press("Тартар из лосося — 690 ₽")
    assert await logged.press("Удалить") == ["Удалить блюдо «Тартар из лосося»?"]
    logged.api.returns["delete_dish"] = result
    assert await logged.press("Удалить") == [expected]
    assert logged.api.called("delete_dish") == [("tok", "d-tartare")]


# Просмотр


async def test_view_commands(logged):
    logged.api.returns["get_stop_list"] = [stop_item()]
    assert await logged.send("/stoplist") == ["В стопе:\nТартар из лосося — просрочен продукт «Лосось охлаждённый»"]

    logged.api.returns["get_stock"] = [
        {"productId": "p-cream", "productName": "Сливки 33%", "unit": "L", "fresh": 2.6, "expired": 0,
         "freshValue": 988.0, "batches": 1, "nearestExpiry": None},
    ]
    logged.api.returns["get_batches"] = []
    assert await logged.send("/stock СЛИВ") == [
        "Сливки 33%\nГодный остаток: 2,6 л на 988 ₽\nПартий: 1\nПартий с остатком нет."
    ]
    assert await logged.send("/stock икра") == ["Продукт «икра» не найден. Все остатки: /stock"]
    assert (await logged.send("/stock"))[0].startswith("Остатки:\nСливки 33% — 2,6 л")

    logged.api.returns["get_hot_offers"] = None
    assert await logged.send("/hot") == ["Горячие предложения скоро появятся."]
    logged.api.returns["get_hot_offers"] = [
        {"dishId": "d1", "dishName": "Том ям", "photoUrl": None, "price": 690, "score": 1.0, "portions": 8,
         "availablePortions": 8, "valueAtRisk": 1992.0, "reason": "Основа том ям истекает через 5 ч.",
         "ingredients": []},
    ]
    assert await logged.send("/hot") == [
        "Горячие предложения:\n\nТом ям — продать 8 порций, под угрозой списания 1 992 ₽\n"
        "Основа том ям истекает через 5 ч."
    ]

    logged.api.returns["get_purchase_suggestions"] = [
        {"productId": "p-salmon", "productName": "Лосось охлаждённый", "unit": "Kg", "quantity": 5.2,
         "fresh": 2.1, "perDay": 2.4, "hoursCovered": 20.0, "reason": "Остатка хватит на 20 ч."},
    ]
    assert await logged.send("/buy") == [
        "Что заказать:\n\nЛосось охлаждённый — заказать 5,2 кг\nОстатка хватит на 20 ч."
    ]

    logged.api.returns["get_write_off_analytics"] = {
        "days": 7, "total": 380, "byDay": [], "byReason": [],
        "byProduct": [{"productName": "Сливки 33%", "unit": "L", "quantity": 1, "value": 380}],
    }
    assert await logged.send("/losses") == [
        "Списано за 7 дн.: 380 ₽\n\nБольше всего потерь:\n1. Сливки 33% — 1 л, 380 ₽"
    ]
    assert logged.api.calls[-1] == ("get_write_off_analytics", ("tok",), {"days": 7})

    logged.api.returns["get_movements"] = []
    assert await logged.send("/journal") == ["Журнал пуст."]


async def test_stale_button(logged):
    assert await logged.click("sp:done") == ["Эта кнопка уже не работает. Начните действие заново."]
