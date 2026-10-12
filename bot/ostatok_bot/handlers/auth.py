"""/login: логин, затем пароль. Сообщение с паролем удаляется до запроса к API.

Блокировку после неверных паролей ведёт сервер по логину; бот только пересказывает
attemptsLeft и retryAfterMinutes из ответа.
"""

from __future__ import annotations

import logging

from aiogram import Bot, Router
from aiogram.filters import Command
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import Message

from ..access import commands_for, role_title
from ..api import ApiClient, TooManyRequests, Unauthorized, ValidationFailed, restaurant_of
from ..commands import show_role_commands
from ..storage import Storage
from ..texts import api_error_text

log = logging.getLogger(__name__)
router = Router(name="auth")

RETRY = "Попробуйте ещё раз: /login"


class LoginForm(StatesGroup):
    login = State()
    password = State()


def wrong_password_text(exc: Unauthorized) -> str:
    left = exc.attempts_left
    if left is None:
        return f"Неверный логин или пароль. {RETRY}"
    return f"Неверный логин или пароль. Попыток осталось: {left}. {RETRY}"


def locked_text(exc: TooManyRequests) -> str:
    minutes = exc.retry_after_minutes
    if minutes is None:
        return "Слишком много попыток входа. Подождите минуту и попробуйте снова: /login"
    return f"Слишком много неверных попыток. Вход откроется через {minutes} мин."


@router.message(Command("login"))
async def cmd_login(message: Message, state: FSMContext) -> None:
    await state.set_state(LoginForm.login)
    await message.answer("Введите логин. Отменить: /cancel")


@router.message(LoginForm.login)
async def got_login(message: Message, state: FSMContext) -> None:
    login = (message.text or "").strip()
    if not login or login.startswith("/"):
        await message.answer("Логин не может быть пустым. Введите логин или /cancel.")
        return
    await state.update_data(login=login)
    await state.set_state(LoginForm.password)
    await message.answer("Введите пароль. Сообщение с паролем я сразу удалю.")


@router.message(LoginForm.password)
async def got_password(message: Message, state: FSMContext, api: ApiClient, db: Storage, bot: Bot) -> None:
    password = message.text or ""
    try:
        await message.delete()
    except Exception:
        log.warning("Не удалось удалить сообщение с паролем в чате %s", message.chat.id)
        await message.answer("Не получилось удалить сообщение с паролем, удалите его сами.")
    if not password.strip():
        await message.answer("Пароль не может быть пустым. Введите пароль или /cancel.")
        return
    if password.startswith("/"):
        await message.answer("Сейчас идёт вход. Введите пароль или прервите вход: /cancel")
        return

    login = (await state.get_data())["login"]
    try:
        result = await api.login(login, password)
    except Unauthorized as exc:
        await state.clear()
        await message.answer(wrong_password_text(exc))
        return
    except TooManyRequests as exc:
        await state.clear()
        await message.answer(locked_text(exc))
        return
    except ValidationFailed as exc:
        await state.clear()
        await message.answer(api_error_text(exc) + "\n" + RETRY)
        return

    try:
        me = await api.get_me(result.token)
        db.save_session(
            message.chat.id, result.token, result.expires_at,
            login=me.get("login", login), title=me.get("title", ""), role=me.get("role", ""),
            sections=me.get("sections", []), restaurant_id=restaurant_of(result.token),
            restaurant=me.get("restaurant", ""),
        )
    finally:
        # Пароль уже принят сервером: что бы ни случилось дальше, следующее сообщение — не пароль.
        await state.clear()
    session = db.get_session(message.chat.id)
    await show_role_commands(bot, session)
    available = ", ".join(f"/{c.command}" for c in commands_for(session) if c.command not in ("cancel", "logout"))
    where = f", {session.restaurant}" if session.restaurant else ""
    await message.answer(f"Вы вошли: {session.login}, {role_title(session)}{where}.\nДоступно: {available}")
