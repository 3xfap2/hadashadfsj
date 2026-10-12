"""Меню команд в Telegram для каждого чата: гостю — вход, вошедшему — команды его роли."""

from __future__ import annotations

import logging

from aiogram import Bot
from aiogram.types import BotCommandScopeChat

from .access import commands_for
from .storage import Session

log = logging.getLogger(__name__)


async def show_role_commands(bot: Bot, session: Session) -> None:
    try:
        await bot.set_my_commands(commands_for(session), scope=BotCommandScopeChat(chat_id=session.chat_id))
    except Exception:
        log.warning("Не удалось обновить меню команд в чате %s", session.chat_id)


async def show_guest_commands(bot: Bot, chat_id: int) -> None:
    """Убрать меню чата: Telegram покажет общее меню гостя (/start, /login)."""
    try:
        await bot.delete_my_commands(scope=BotCommandScopeChat(chat_id=chat_id))
    except Exception:
        log.warning("Не удалось сбросить меню команд в чате %s", chat_id)
