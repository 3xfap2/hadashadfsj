"""Списки продуктов и блюд кнопками с постраничной навигацией."""

from __future__ import annotations

from aiogram.types import InlineKeyboardMarkup

from ..fmt import unit_label
from ..keyboards import cancel_row, paged


def product_cache(products: list[dict]) -> dict[str, dict]:
    """Продукты для текущего диалога: id -> название, единица, норматив, цена, состав заготовки,
    срок после вскрытия (его нужно вернуть при правке продукта, иначе сервер его сотрёт)."""
    return {
        p["id"]: {
            "name": p["name"], "unit": p["unit"], "shelfLifeHours": p["shelfLifeHours"], "cost": p["cost"],
            "prepRecipe": p.get("prepRecipe") or [], "openedShelfLifeHours": p.get("openedShelfLifeHours"),
        }
        for p in products
    }


def sorted_ids(items: dict[str, dict]) -> list[str]:
    return sorted(items, key=lambda i: items[i]["name"].lower())


def product_picker(
    products: dict[str, dict],
    page: int,
    pick_prefix: str,
    nav_prefix: str,
    extra: list[list[tuple[str, str]]] | None = None,
) -> InlineKeyboardMarkup:
    buttons = [
        (f"{products[pid]['name']} ({unit_label(products[pid]['unit'])})", f"{pick_prefix}:{pid}")
        for pid in sorted_ids(products)
    ]
    return paged(buttons, page, nav_prefix, (extra or []) + [cancel_row()])


def page_from(data: str) -> int:
    try:
        return int(data.rsplit(":", 1)[1])
    except (IndexError, ValueError):
        return 0
