"""Инлайн-клавиатуры."""

from __future__ import annotations

from aiogram.types import InlineKeyboardButton, InlineKeyboardMarkup

from .fmt import fmt_qty

PAGE_SIZE = 8
CANCEL = "cancel"


def button(text: str, data: str) -> InlineKeyboardButton:
    return InlineKeyboardButton(text=text, callback_data=data)


def rows(*rows_: list[tuple[str, str]]) -> InlineKeyboardMarkup:
    return InlineKeyboardMarkup(inline_keyboard=[[button(t, d) for t, d in row] for row in rows_ if row])


def cancel_row() -> list[tuple[str, str]]:
    return [("Отмена", CANCEL)]


def paged(
    items: list[tuple[str, str]],
    page: int,
    nav_prefix: str,
    extra: list[list[tuple[str, str]]] | None = None,
) -> InlineKeyboardMarkup:
    """По 8 кнопок на страницу, «Назад»/«Дальше» шлют f"{nav_prefix}:{страница}"."""
    pages = max(1, -(-len(items) // PAGE_SIZE))
    page = min(max(page, 0), pages - 1)
    start = page * PAGE_SIZE
    keyboard = [[button(text, data)] for text, data in items[start:start + PAGE_SIZE]]
    nav = []
    if page > 0:
        nav.append(button("Назад", f"{nav_prefix}:{page - 1}"))
    if page < pages - 1:
        nav.append(button("Дальше", f"{nav_prefix}:{page + 1}"))
    if nav:
        keyboard.append(nav)
    for row in extra or []:
        keyboard.append([button(t, d) for t, d in row])
    return InlineKeyboardMarkup(inline_keyboard=keyboard)


def confirm(yes_text: str, yes_data: str) -> InlineKeyboardMarkup:
    return rows([(yes_text, yes_data)], cancel_row())


def batch_actions_keyboard(write_off: list[dict], to_open: list[dict]) -> InlineKeyboardMarkup | None:
    """«Списать» под просроченными и «Вскрыть» под партиями, которые можно вскрыть.
    Если кнопка одна, без названия продукта: оно и так в сообщении."""
    if not to_open:
        return write_off_keyboard(write_off) if write_off else None
    if not write_off and len(to_open) == 1:
        return rows([("Вскрыть", f"op:{to_open[0]['id']}")])
    return rows(
        *[[(f"Списать: {b['productName']}, {fmt_qty(b['remaining'], b['unit'])}", f"wo:{b['id']}")] for b in write_off],
        *[[(f"Вскрыть: {b['productName']}, {fmt_qty(b['remaining'], b['unit'])}", f"op:{b['id']}")] for b in to_open],
    )


def write_off_keyboard(batches: list[dict]) -> InlineKeyboardMarkup:
    """Кнопка «Списать» на каждую партию. Если партия одна — просто «Списать»."""
    if len(batches) == 1:
        return rows([("Списать", f"wo:{batches[0]['id']}")])
    return rows(*[
        [(f"Списать: {b['productName']}, {fmt_qty(b['remaining'], b['unit'])}", f"wo:{b['id']}")]
        for b in batches
    ])
