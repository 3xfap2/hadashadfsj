"""Тестовый стенд: настоящий Dispatcher бота, подставная сессия Telegram
и подставной клиент API."""

from __future__ import annotations

import inspect
import itertools
from pathlib import Path
from datetime import datetime, timedelta, timezone
from typing import Any
from zoneinfo import ZoneInfo

import pytest
from aiogram import Bot
from aiogram.client.session.base import BaseSession
from aiogram.fsm.storage.memory import MemoryStorage
from aiogram.methods import (
    AnswerCallbackQuery,
    DeleteMessage,
    EditMessageReplyMarkup,
    EditMessageText,
    SendMessage,
)
from aiogram.types import CallbackQuery, Chat, InlineKeyboardMarkup, Message, Update, User

from ostatok_bot.api import ApiClient
from ostatok_bot.app import build_dispatcher
from ostatok_bot.config import Config
from ostatok_bot.storage import Storage

CHAT_ID = 100
NOW = datetime(2026, 10, 9, 12, 0, tzinfo=timezone.utc)
TZ = ZoneInfo("Europe/Moscow")


class FakeSession(BaseSession):
    """Запоминает запросы бота к Telegram и отвечает правдоподобно."""

    def __init__(self) -> None:
        super().__init__()
        self.requests: list[Any] = []
        self.fail_delete = False
        self._ids = itertools.count(1000)

    async def make_request(self, bot, method, timeout=None):
        self.requests.append(method)
        if isinstance(method, SendMessage):
            return Message(
                message_id=next(self._ids),
                date=NOW,
                chat=Chat(id=method.chat_id, type="private"),
                text=method.text,
                reply_markup=method.reply_markup,
            )
        if isinstance(method, EditMessageText):
            return Message(
                message_id=method.message_id or 1,
                date=NOW,
                chat=Chat(id=method.chat_id or CHAT_ID, type="private"),
                text=method.text,
                reply_markup=method.reply_markup,
            )
        if isinstance(method, DeleteMessage) and self.fail_delete:
            raise RuntimeError("delete failed")
        return True

    async def close(self) -> None:
        pass

    async def stream_content(self, *args, **kwargs):
        raise NotImplementedError
        yield b""


class FakeApi:
    """Подставной клиент: ответы задаются в returns[имя метода]. Исключение — бросается.
    Обращение к методу, которого нет у ApiClient, — ошибка теста."""

    def __init__(self) -> None:
        self.returns: dict[str, Any] = {}
        self.calls: list[tuple[str, tuple, dict]] = []

    def __getattr__(self, name: str):
        if not hasattr(ApiClient, name):
            raise AttributeError(f"У ApiClient нет метода {name}")

        async def method(*args, **kwargs):
            self.calls.append((name, args, kwargs))
            value = self.returns.get(name)
            if isinstance(value, BaseException):
                raise value
            if callable(value):
                value = value(*args, **kwargs)
                # Асинхронная подстановка: тест может что-то сделать, пока «запрос идёт».
                return await value if inspect.isawaitable(value) else value
            return value

        return method

    def called(self, name: str) -> list[tuple]:
        return [args for n, args, _ in self.calls if n == name]


_dispatcher = None


def shared_dispatcher():
    """Роутеры aiogram подключаются к одному Dispatcher, поэтому он общий на все тесты,
    а API, база и состояния диалогов у каждого теста свои."""
    global _dispatcher
    if _dispatcher is None:
        config = Config("42:TEST", "http://api", 30, Path("unused"), TZ)
        _dispatcher = build_dispatcher(None, None, config)
    return _dispatcher


class Harness:
    def __init__(self) -> None:
        self.session = FakeSession()
        self.bot = Bot("42:TEST", session=self.session)
        self.api = FakeApi()
        self.api.returns["get_me"] = ME
        self.db = Storage(":memory:")
        self.dp = shared_dispatcher()
        self.dp.workflow_data.update(api=self.api, db=self.db)
        self.dp.fsm.storage = MemoryStorage()
        self.user = User(id=CHAT_ID, is_bot=False, first_name="Админ")
        self.chat = Chat(id=CHAT_ID, type="private")
        self._ids = itertools.count(1)
        self.last_markup: InlineKeyboardMarkup | None = None

    def login(self, token: str = "user-token", hours: int = 8, role: str = "All") -> None:
        """Войти без диалога. role — роль из ROLE_SECTIONS; All — все разделы сразу, для проверки диалогов."""
        self.db.save_session(
            CHAT_ID, token, datetime.now(timezone.utc) + timedelta(hours=hours),
            login=role.lower(), title=role, role=role, sections=ROLE_SECTIONS[role],
            restaurant_id=RESTAURANT, restaurant="Демо-ресторан",
        )

    async def send(self, text: str) -> list[str]:
        start = len(self.session.requests)
        message = Message(message_id=next(self._ids), date=NOW, chat=self.chat, from_user=self.user, text=text)
        await self.dp.feed_update(self.bot, Update(update_id=next(self._ids), message=message))
        return self._replies(start)

    async def send_media(self, **fields) -> list[str]:
        """Сообщение с фото (photo=[PhotoSize, ...]) или файлом (document=Document)."""
        start = len(self.session.requests)
        message = Message(message_id=next(self._ids), date=NOW, chat=self.chat, from_user=self.user, **fields)
        await self.dp.feed_update(self.bot, Update(update_id=next(self._ids), message=message))
        return self._replies(start)

    async def click(self, data: str) -> list[str]:
        start = len(self.session.requests)
        message = Message(message_id=next(self._ids), date=NOW, chat=self.chat, text="кнопки")
        callback = CallbackQuery(
            id=str(next(self._ids)), from_user=self.user, chat_instance="ci", data=data, message=message
        )
        await self.dp.feed_update(self.bot, Update(update_id=next(self._ids), callback_query=callback))
        return self._replies(start)

    async def press(self, label: str) -> list[str]:
        """Нажать кнопку с этим текстом на последней клавиатуре."""
        return await self.click(self.button_data(label))

    def button_data(self, label: str) -> str:
        assert self.last_markup, "Клавиатуры нет"
        for row in self.last_markup.inline_keyboard:
            for b in row:
                if b.text == label:
                    return b.callback_data
        labels = [b.text for row in self.last_markup.inline_keyboard for b in row]
        raise AssertionError(f"Нет кнопки «{label}», есть: {labels}")

    def buttons(self) -> list[str]:
        return [b.text for row in (self.last_markup.inline_keyboard if self.last_markup else []) for b in row]

    def _replies(self, start: int) -> list[str]:
        out = []
        for req in self.session.requests[start:]:
            if isinstance(req, (SendMessage, EditMessageText)):
                out.append(req.text)
                if req.reply_markup is not None:
                    self.last_markup = req.reply_markup
            elif isinstance(req, EditMessageReplyMarkup):
                pass
            elif isinstance(req, AnswerCallbackQuery) and req.text:
                out.append(req.text)
        return out

    def deleted(self) -> list[DeleteMessage]:
        return [r for r in self.session.requests if isinstance(r, DeleteMessage)]


@pytest.fixture
def h() -> Harness:
    return Harness()


@pytest.fixture(autouse=True)
def forget_albums():
    """Память об альбомах общая на процесс: у каждого теста она своя."""
    from ostatok_bot.handlers import photo_supply

    photo_supply._albums.clear()


# Данные, похожие на ответы API

SALMON = {"id": "p-salmon", "name": "Лосось охлаждённый", "unit": "Kg", "shelfLifeHours": 48, "cost": 1900}
CREAM = {"id": "p-cream", "name": "Сливки 33%", "unit": "L", "shelfLifeHours": 72, "cost": 380}
EGG = {"id": "p-egg", "name": "Яйцо", "unit": "Pcs", "shelfLifeHours": 240, "cost": 12}
PRODUCTS = [SALMON, CREAM, EGG]

TARTARE = {"id": "d-tartare", "name": "Тартар из лосося", "price": 690, "photoUrl": "/dishes/tartare.jpg",
           "category": "Холодные закуски", "manuallyStopped": False,
           "recipe": [{"productId": "p-salmon", "quantity": 0.12}]}
CARBONARA = {"id": "d-carbonara", "name": "Карбонара", "price": 540, "photoUrl": None,
             "recipe": [{"productId": "p-cream", "quantity": 0.05}, {"productId": "p-egg", "quantity": 1}]}
DISHES = [TARTARE, CARBONARA]


def batch(id_="b1", status="Expired", hours=-3.0, product=CREAM, remaining=0.9, supply="П-0413", value=342.0) -> dict:
    return {
        "id": id_, "productId": product["id"], "productName": product["name"], "unit": product["unit"],
        "supplyNumber": supply, "quantity": 2.5, "remaining": remaining,
        "expiresAt": "2026-10-09T09:00:00+00:00", "hoursLeft": hours, "status": status, "value": value,
    }


def stop_item(dish=TARTARE, status="Stop", portions=0, reason="Expired", product=SALMON, forecast=None) -> dict:
    return {
        "dishId": dish["id"], "dishName": dish["name"], "price": dish["price"], "status": status,
        "portions": portions, "limitingProductId": product["id"], "limitingProductName": product["name"],
        "reason": reason if status == "Stop" else None,
        "photoUrl": dish.get("photoUrl"), "stopForecastAt": forecast,
    }


PLACES = [f"Стол {i}" for i in range(1, 13)] + ["Бар"]
RESTAURANT = "0d3a0c1e-0000-4000-8000-000000000001"

# Разделы ролей — как их вернул GET /api/me развёрнутого API для демо-записей.
ROLE_SECTIONS = {
    "Chef": ("today", "stock", "menu", "kitchen", "purchasing", "analytics", "catalog"),
    "Manager": ("today", "stock", "menu", "kitchen", "purchasing", "analytics", "catalog", "staff"),
    "Cook": ("today", "menu", "kitchen"),
    "Storekeeper": ("today", "stock", "purchasing"),
    "Waiter": ("orders", "menu"),
}
ROLE_SECTIONS["All"] = tuple(sorted({s for sections in ROLE_SECTIONS.values() for s in sections}))

ME = {
    "login": "demo", "title": "Шеф-повар", "role": "Chef", "sections": list(ROLE_SECTIONS["Chef"]),
    "shift": {"start": "12:00", "end": "24:00"}, "places": PLACES, "restaurant": "Демо-ресторан",
}


def jwt(restaurant: str = RESTAURANT, login: str = "demo") -> str:
    """Токен с полем restaurant, как у сервера. Подпись боту не нужна."""
    import base64
    import json

    def part(obj):
        return base64.urlsafe_b64encode(json.dumps(obj).encode()).decode().rstrip("=")

    return f"{part({'alg': 'HS256'})}.{part({'restaurant': restaurant, 'sub': login})}.sig"
