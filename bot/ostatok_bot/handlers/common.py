"""/start, /cancel, /logout и общая обработка ошибок API."""

from __future__ import annotations

import logging

from aiogram import Bot, F, Router
from aiogram.filters import Command, CommandStart, ExceptionTypeFilter
from aiogram.fsm.context import FSMContext
from aiogram.types import CallbackQuery, ErrorEvent, Message

from ..access import COMMANDS, role_title
from ..api import ApiError, ApiUnavailable, Forbidden, Unauthorized
from ..commands import show_guest_commands
from ..keyboards import CANCEL
from ..storage import Storage, utcnow
from ..texts import API_DOWN, API_NO_ANSWER, FORBIDDEN, SESSION_EXPIRED, api_error_text

from .util import ack

log = logging.getLogger(__name__)
router = Router(name="common")

GUEST_TEXT = """Бот «Остатка»: склад, сроки годности, заказы и стоп-лист ресторана.

Войдите тем же логином и паролем, что на сайте: /login
Бот покажет команды вашей роли и будет сам писать о просроченных партиях и изменениях стоп-листа."""


def start_text(session) -> str:
    lines = [f"{session.login}, {role_title(session)}" + (f", {session.restaurant}" if session.restaurant else ""), ""]
    lines += [f"/{name} — {text.lower()}" for name, text, section in COMMANDS if section is None or session.allows(section)]
    lines += ["", "/stock сливки — остаток одного продукта"]
    return "\n".join(lines)


@router.message(CommandStart())
async def cmd_start(message: Message, db: Storage) -> None:
    session = db.get_session(message.chat.id)
    if session and session.is_valid(utcnow()):
        await message.answer(start_text(session))
    else:
        await message.answer(GUEST_TEXT)


SENDING = "Заказ или поставка уже отправляются, отменить нельзя. Подождите ответа сервера."


def is_sending(current: str | None) -> bool:
    """Шаг «отправляется» заказа или поставки: запрос уже ушёл, сброс диалога его не остановит."""
    return bool(current) and current.endswith(":sending")


@router.message(Command("cancel"))
async def cmd_cancel(message: Message, state: FSMContext) -> None:
    current = await state.get_state()
    if current is None:
        await message.answer("Сейчас нечего отменять.")
        return
    if is_sending(current):
        await message.answer(SENDING)
        return
    await state.clear()
    await message.answer("Действие отменено.")


@router.callback_query(F.data == CANCEL)
async def cb_cancel(callback: CallbackQuery, state: FSMContext) -> None:
    if is_sending(await state.get_state()):
        await callback.answer(SENDING, show_alert=True)
        return
    await state.clear()
    await callback.answer()
    await callback.message.edit_text("Действие отменено.")


@router.message(Command("logout"))
async def cmd_logout(message: Message, state: FSMContext, db: Storage, bot: Bot) -> None:
    db.delete_session(message.chat.id)
    await state.clear()
    await show_guest_commands(bot, message.chat.id)
    await message.answer("Вы вышли. Уведомления в этот чат больше не приходят.")


def _chat_target(event: ErrorEvent):
    update = event.update
    if update.callback_query:
        return update.callback_query.message, update.callback_query
    return update.message, None


async def _reply(event: ErrorEvent, text: str) -> None:
    message, callback = _chat_target(event)
    if callback:
        await ack(callback)
    if message:
        await message.answer(text)


@router.error(ExceptionTypeFilter(Unauthorized))
async def on_unauthorized(event: ErrorEvent, db: Storage, bot: Bot, state: FSMContext | None = None) -> None:
    # Токен отозван или истёк: повтор запроса не поможет, нужно войти заново.
    message, _ = _chat_target(event)
    if state:
        await state.clear()
    # Удаляем только сессию с тем токеном, на который пришёл 401: пока шёл запрос, чат мог войти заново.
    if message and not db.delete_session(message.chat.id, event.exception.token):
        await _reply(event, "Вход в этом чате обновился, пока шёл запрос. Повторите действие.")
        return
    if message:
        await show_guest_commands(bot, message.chat.id)
    await _reply(event, SESSION_EXPIRED)


@router.error(ExceptionTypeFilter(Forbidden))
async def on_forbidden(event: ErrorEvent, state: FSMContext | None = None) -> None:
    if state:
        await state.clear()
    await _reply(event, FORBIDDEN)


@router.error(ExceptionTypeFilter(ApiUnavailable))
async def on_api_down(event: ErrorEvent) -> None:
    log.warning("%s", event.exception)
    await _reply(event, API_NO_ANSWER if event.exception.unsure else API_DOWN)


@router.error(ExceptionTypeFilter(ApiError))
async def on_api_error(event: ErrorEvent) -> None:
    log.warning("Неожиданный ответ API: %s %r", event.exception, event.exception.body)
    await _reply(event, api_error_text(event.exception))


@router.error()
async def on_any_error(event: ErrorEvent) -> None:
    log.exception("Ошибка при обработке обновления", exc_info=event.exception)
    await _reply(event, "Что-то пошло не так. Попробуйте ещё раз или начните заново.")
