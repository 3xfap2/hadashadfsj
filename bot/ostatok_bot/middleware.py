"""Пропускает к командам только вошедшие чаты и подставляет их токен и сессию (роль, разделы)."""

from __future__ import annotations

from typing import Any, Awaitable, Callable

from aiogram import BaseMiddleware
from aiogram.fsm.context import FSMContext
from aiogram.types import CallbackQuery, Message, TelegramObject

from .commands import show_guest_commands
from .storage import Storage, utcnow
from .texts import LOGIN_REQUIRED, SESSION_EXPIRED

PUBLIC_COMMANDS = {"start", "login", "cancel"}
LOGIN_STATE_PREFIX = "LoginForm:"
PRIVATE_ONLY = "Бот работает только в личном чате: логин, пароль и данные ресторана нельзя показывать в группе. Напишите мне лично."


def command_name(text: str | None) -> str | None:
    if not text or not text.startswith("/"):
        return None
    return text[1:].split(maxsplit=1)[0].split("@", 1)[0].lower() if len(text) > 1 else None


class AuthMiddleware(BaseMiddleware):
    """Хранилище сессий берёт из данных диспетчера (ключ db)."""

    def __init__(self, clock: Callable = utcnow):
        self._clock = clock

    async def __call__(
        self,
        handler: Callable[[TelegramObject, dict[str, Any]], Awaitable[Any]],
        event: TelegramObject,
        data: dict[str, Any],
    ) -> Any:
        state: FSMContext | None = data.get("state")
        # Только личные чаты: в группе логин и пароль видели бы все, а сессия и уведомления стали бы общими.
        chat = event.chat if isinstance(event, Message) else getattr(getattr(event, "message", None), "chat", None)
        if chat is not None and chat.type != "private":
            if isinstance(event, CallbackQuery):
                await event.answer(PRIVATE_ONLY, show_alert=True)
            elif command_name(event.text):
                await event.answer(PRIVATE_ONLY)
            return None
        if isinstance(event, Message):
            if command_name(event.text) in PUBLIC_COMMANDS:
                return await handler(event, data)
            current = await state.get_state() if state else None
            if current and current.startswith(LOGIN_STATE_PREFIX):
                return await handler(event, data)
            chat_id = event.chat.id
        elif isinstance(event, CallbackQuery):
            chat_id = event.message.chat.id if event.message else event.from_user.id
        else:
            return await handler(event, data)

        db: Storage = data["db"]
        session = db.get_session(chat_id)
        if session and session.is_valid(self._clock()):
            data["token"] = session.token
            data["session"] = session
            return await handler(event, data)

        if session:
            db.delete_session(chat_id, session.token)
            if data.get("bot"):
                await show_guest_commands(data["bot"], chat_id)
        text = SESSION_EXPIRED if session else LOGIN_REQUIRED
        if state:
            await state.clear()
        if isinstance(event, CallbackQuery):
            await event.answer(text, show_alert=True)
        else:
            await event.answer(text)
        return None
