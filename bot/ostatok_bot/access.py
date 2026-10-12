"""Что доступно роли. Источник правды — сервер: разделы приходят в GET /api/me (sections),
а на запрещённое действие API отвечает 403. Бот по разделам заранее прячет команды и
останавливает диалог на первом шаге, а не после ввода всех данных."""

from __future__ import annotations

from aiogram.types import BotCommand, CallbackQuery, Message

from .storage import Session

# Раздел кабинета -> кому он доступен, в дательном падеже (для текста отказа).
WHO = {
    "orders": "официанту",
    "stock": "управляющему, шефу и кладовщику",
    "purchasing": "управляющему, шефу и кладовщику",
    "kitchen": "управляющему, шефу и повару",
    "analytics": "управляющему и шефу",
    "catalog": "управляющему и шефу",
    "staff": "управляющему",
}

ROLE_TITLES = {
    "Manager": "управляющий",
    "Chef": "шеф-повар",
    "Cook": "повар",
    "Storekeeper": "кладовщик",
    "Waiter": "официант",
}

# Команда, описание, раздел (None — доступна всем вошедшим).
COMMANDS: list[tuple[str, str, str | None]] = [
    ("stoplist", "Стоп-лист", None),
    ("order", "Создать заказ", "orders"),
    ("close", "Закрыть стол", "orders"),
    ("expiring", "Истекающие и просроченные партии", None),
    ("stock", "Остатки на складе", None),
    ("supply", "Принять поставку", "stock"),
    ("incoming", "Поставки на подтверждение", "stock"),
    ("inventory", "Инвентаризация", "stock"),
    ("preps", "Заготовки", "kitchen"),
    ("dishes", "Блюда и техкарты", None),
    ("products", "Продукты", None),
    ("hot", "Горячие предложения", None),
    ("buy", "Что заказать", "purchasing"),
    ("losses", "Списания за неделю", "analytics"),
    ("stopcost", "Потери из-за стопа", "analytics"),
    ("journal", "Журнал операций", None),
    ("menu_link", "Ссылка на меню для гостей", "staff"),
    ("cancel", "Прервать действие", None),
    ("logout", "Выйти", None),
]

GUEST_COMMANDS = [BotCommand(command="start", description="Что умеет бот"), BotCommand(command="login", description="Войти")]


def commands_for(session: Session) -> list[BotCommand]:
    return [
        BotCommand(command=name, description=text)
        for name, text, section in COMMANDS
        if section is None or session.allows(section)
    ]


def denied_text(section: str, action: str) -> str:
    return f"Нет доступа: {action} можно только {WHO.get(section, 'другой роли')}."


async def check(event: Message | CallbackQuery, session: Session | None, section: str, action: str) -> bool:
    """True, если роли доступен раздел. Иначе отвечает пользователю и возвращает False."""
    if session is not None and session.allows(section):
        return True
    text = denied_text(section, action)
    if isinstance(event, CallbackQuery):
        await event.answer(text, show_alert=True)
    else:
        await event.answer(text)
    return False


def role_title(session: Session) -> str:
    return session.title or ROLE_TITLES.get(session.role, session.role)
