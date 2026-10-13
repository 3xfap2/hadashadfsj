"""Вспомогательные функции для обработчиков."""

from __future__ import annotations

import logging
from typing import Callable

from aiogram.exceptions import TelegramBadRequest
from aiogram.types import CallbackQuery, InlineKeyboardMarkup, Message

log = logging.getLogger(__name__)

TELEGRAM_LIMIT = 4000


def split_text(text: str, limit: int = TELEGRAM_LIMIT) -> list[str]:
    """Режет длинный текст по строкам, чтобы уложиться в лимит Telegram."""
    chunks, current = [], ""
    for line in text.split("\n"):
        # Одна строка длиннее лимита (например, «Нет на складе: …» через запятую) режется по символам.
        while len(line) > limit:
            if current:
                chunks.append(current)
                current = ""
            chunks.append(line[:limit])
            line = line[limit:]
        candidate = f"{current}\n{line}" if current else line
        if len(candidate) > limit and current:
            chunks.append(current)
            current = line
        else:
            current = candidate
    chunks.append(current)
    return chunks


def result_text(render: Callable[[], str], done: str) -> str:
    """Текст ответа после уже выполненного изменения. Если ответ сервера не удалось показать, действие
    всё равно выполнено: «что-то пошло не так» здесь привело бы к повтору и дублю."""
    try:
        return render()
    except Exception:
        log.exception("Ответ сервера после выполненного действия не удалось показать")
        return f"{done} Показать ответ сервера не удалось: проверьте результат в /journal, прежде чем повторять."


async def send(message: Message, text: str, reply_markup: InlineKeyboardMarkup | None = None) -> None:
    """Ответ в чат; клавиатура цепляется к последнему куску."""
    chunks = split_text(text)
    for chunk in chunks[:-1]:
        await message.answer(chunk)
    await message.answer(chunks[-1], reply_markup=reply_markup)


async def ack(callback: CallbackQuery, text: str | None = None, show_alert: bool = False) -> None:
    """Ответить на нажатие кнопки. Повторный или запоздалый ответ (после долгого запроса к API)
    Telegram отклоняет — это не повод ронять обработчик."""
    try:
        await callback.answer(text, show_alert=show_alert)
    except TelegramBadRequest as exc:
        log.debug("Ответ на нажатие не принят: %s", exc)


async def show(callback: CallbackQuery, text: str, reply_markup: InlineKeyboardMarkup | None = None) -> None:
    """Заменить сообщение с кнопками новым шагом диалога."""
    await ack(callback)
    if len(text) <= TELEGRAM_LIMIT:
        try:
            await callback.message.edit_text(text, reply_markup=reply_markup)
        except TelegramBadRequest as exc:
            # Двойное нажатие: второй раз показывается то же самое — это не ошибка.
            if "message is not modified" not in str(exc):
                raise
    else:
        await callback.message.edit_reply_markup(reply_markup=None)
        await send(callback.message, text, reply_markup)
