"""/stoplist, /expiring, /stock, /hot, /buy, /losses, /stopcost, /journal, /menu_link."""

from __future__ import annotations

import logging
from typing import Callable
from zoneinfo import ZoneInfo

from aiogram import Router
from aiogram.filters import Command, CommandObject
from aiogram.types import InlineKeyboardMarkup, Message

from .. import access, texts
from ..api import ApiClient, ApiError, Unauthorized
from ..fmt import plural
from ..keyboards import batch_actions_keyboard
from ..storage import Session, utcnow
from .util import send

log = logging.getLogger(__name__)

router = Router(name="view")


@router.message(Command("stoplist"))
async def cmd_stoplist(
    message: Message, api: ApiClient, token: str, tz: ZoneInfo, clock: Callable = utcnow
) -> None:
    await send(message, texts.stop_list_text(await api.get_stop_list(token), clock(), tz))


@router.message(Command("expiring"))
async def cmd_expiring(message: Message, api: ApiClient, token: str, session: Session) -> None:
    batches = texts.expiring_batches(await api.get_batches(token))
    await send(message, texts.expiring_text(batches), actions_for(batches, session))


def actions_for(batches: list[dict], session: Session) -> InlineKeyboardMarkup | None:
    """Списывать и вскрывать партии может только склад: остальным кнопки не показываются."""
    if not session.allows("stock"):
        return None
    expired = [b for b in batches if b["status"] == "Expired"]
    return batch_actions_keyboard(expired, [b for b in batches if texts.can_open(b)])


@router.message(Command("stock"))
async def cmd_stock(
    message: Message, command: CommandObject, api: ApiClient, token: str, tz: ZoneInfo, session: Session
) -> None:
    items = await api.get_stock(token)
    query = (command.args or "").strip()
    if not query:
        await send(message, texts.stock_summary_text(items))
        return
    found = [i for i in items if query.lower() in i["productName"].lower()]
    if not found:
        await message.answer(f"Продукт «{query}» не найден. Все остатки: /stock")
        return
    # Карточка продукта: остаток и его партии; у партий — «Списать» и «Вскрыть».
    batches = await api.get_batches(token)
    cards, shown = [], []
    for item in found:
        own = [b for b in batches if b["productId"] == item["productId"]]
        shown += own
        cards.append(texts.stock_item_text(item, tz) + "\n" + texts.product_batches_text(own, tz))
    await send(message, "\n\n".join(cards), actions_for(shown, session))


@router.message(Command("hot"))
async def cmd_hot(message: Message, api: ApiClient, token: str) -> None:
    offers = await api.get_hot_offers(token)
    text = texts.hot_offers_text(offers)
    if offers is not None:
        plan = await hot_plan(api, token)
        if plan:
            text = f"{text}\n\n{plan}"
    await send(message, text)


PLAN_UNAVAILABLE = "Итог плана спасения сейчас недоступен."


async def hot_plan(api: ApiClient, token: str) -> str:
    """Итог плана спасения. Без него сами предложения всё равно полезны, поэтому сбой плана /hot не роняет;
    но 401 уходит дальше — общий обработчик попросит войти заново."""
    try:
        plan = await api.get_hot_plan(token)
    except Unauthorized:
        raise
    except ApiError as exc:
        log.warning("План спасения не получен: %s %r", exc, exc.body)
        return PLAN_UNAVAILABLE
    try:
        return texts.hot_plan_text(plan)
    except (KeyError, TypeError, ValueError, ArithmeticError) as exc:
        log.warning("План спасения в неожиданном формате: %r (%s)", plan, exc)
        return PLAN_UNAVAILABLE


@router.message(Command("buy"))
async def cmd_buy(message: Message, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(message, session, "purchasing", "смотреть закупки"):
        return
    await send(message, texts.purchase_text(await api.get_purchase_suggestions(token)))


@router.message(Command("losses"))
async def cmd_losses(message: Message, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(message, session, "analytics", "смотреть аналитику"):
        return
    report = await api.get_write_off_analytics(token, days=texts.LOSSES_DAYS)
    await send(message, texts.losses_text(report))


@router.message(Command("stopcost"))
async def cmd_stop_cost(message: Message, api: ApiClient, token: str, session: Session) -> None:
    if not await access.check(message, session, "analytics", "смотреть аналитику"):
        return
    await send(message, texts.stop_cost_text(await api.get_stop_cost(token, days=texts.STOPCOST_DAYS)))


@router.message(Command("journal"))
async def cmd_journal(message: Message, api: ApiClient, token: str, tz: ZoneInfo) -> None:
    movements = await api.get_movements(token, take=texts.JOURNAL_SIZE)
    await send(message, texts.journal_text(movements, tz))


@router.message(Command("menu_link"))
async def cmd_menu_link(message: Message, api: ApiClient, token: str, session: Session, site_url: str) -> None:
    if not await access.check(message, session, "staff", "получать ссылку на меню для гостей"):
        return
    me = await api.get_me(token)
    restaurant_id = me.get("restaurantId") or session.restaurant_id
    if not restaurant_id:
        await message.answer("Сервер не прислал идентификатор ресторана. Ссылку можно взять в кабинете на сайте.")
        return
    name = me.get("restaurant") or session.restaurant
    lines = [f"Меню для гостей «{name}»:", f"{site_url}/menu/{restaurant_id}", ""]
    try:
        # Открыть меню так же, как гость, без входа: заодно видно, что ссылка рабочая.
        menu = await api.get_public_menu(restaurant_id)
    except ApiError as exc:
        log.warning("Меню для гостя не открылось: %s", exc)
        lines.append("Проверить меню сейчас не удалось, ссылка от этого не меняется.")
    else:
        dishes = menu.get("dishes") or []
        offers = sum(1 for d in dishes if d.get("offerPrice") is not None)
        lines.append(
            f"Сейчас в меню {len(dishes)} {plural(len(dishes), 'блюдо', 'блюда', 'блюд')}, "
            f"по предложению дня — {offers}. Блюда из стоп-листа гости не видят."
        )
    lines.append("Ссылку можно отправить гостю или напечатать QR-кодом на столы.")
    await message.answer("\n".join(lines))
