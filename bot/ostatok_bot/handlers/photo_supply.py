"""Поставка по фото: накладная или этикетка → строки поставки → номер → подтверждение.

Фото сотрудник склада присылает в любой момент (или на шаге номера в /supply). Бот скачивает снимок,
сервер распознаёт его (POST /api/supplies/import/preview) и сверяет со справочником. Строки с ошибками
не отправляются; остальные уходят обычной поставкой вместе с ценой из накладной.
Каждый снимок расходует лимит ресторана (20 в час), поэтому, пока идёт распознавание, новые фото
не запускают второе, а из альбома распознаётся только первая страница.
"""

from __future__ import annotations

import asyncio
import logging
import time
from zoneinfo import ZoneInfo

import aiohttp

from aiogram import Bot, F, Router
from aiogram.exceptions import TelegramAPIError
from aiogram.filters import StateFilter
from aiogram.fsm.context import FSMContext
from aiogram.fsm.state import State, StatesGroup
from aiogram.types import CallbackQuery, Message, PhotoSize

from .. import access
from ..api import ApiClient, ApiError, ApiUnavailable, NotFound, TooManyRequests, ValidationFailed
from ..fmt import fmt_money, fmt_qty, unit_label
from ..keyboards import cancel_row, rows
from ..storage import Session, Storage
from ..texts import api_error_text
from .pickers import product_cache
from .submit import submit_supply, unfinished_supply_note
from .supply import SYSTEM_PREFIXES, SupplyForm
from .util import send, show

log = logging.getLogger(__name__)
router = Router(name="photo_supply")

MAX_BYTES = 1_000_000   # предел сервера на файл
MAX_SIDE = 1600         # длинная сторона снимка: крупнее не нужно для распознавания
IMAGE_TYPES = {"image/jpeg", "image/png", "image/webp"}
MAX_NUMBER = 40
MANUAL = "примите поставку вручную: /supply"

FILE_TOO_BIG = (
    "Файл больше 1 МБ, сервер его не примет. Сожмите фото до 1600 px по длинной стороне "
    "или пришлите его обычным фото, а не файлом: Telegram сожмёт его сам."
)
PHOTO_TOO_BIG = "Снимок больше 1 МБ, сервер его не примет. Сожмите фото до 1600 px по длинной стороне и пришлите снова."
NOT_CONFIGURED = "Распознавание по фото на сервере не настроено. Примите поставку вручную: /supply"
QUOTA = f"Лимит распознавания исчерпан: не больше 20 снимков в час на ресторан. Подождите или {MANUAL}"
NOT_RECOGNIZED = (
    "Сервер не смог распознать фото. Так бывает из-за снимка (тогда переснимите накладную ближе и ровнее, "
    f"при хорошем свете) или из-за сбоя распознавания (тогда попробуйте через пару минут). Или {MANUAL}"
)
NO_ANSWER = "Сервер не успел распознать фото (снимок мог засчитаться в лимит). Пришлите его ещё раз."
OLD_SERVER = "Эта версия сервера не распознаёт фото. Примите поставку вручную: /supply"
DOWNLOAD_FAILED = "Не удалось скачать фото из Telegram. Пришлите его ещё раз."
BUSY = "Распознаю предыдущее фото, подождите. Следующее пришлите после ответа."

# Чаты, где сейчас идёт распознавание, и альбом, с которого оно началось. Проверка и отметка делаются
# синхронно в начале обработчика: aiogram обрабатывает фото альбома параллельно и успевает несколько раз
# отдать управление, прежде чем дойдёт до состояния диалога. Бот работает в одном экземпляре.
_recognizing: dict[int, str | None] = {}

# Альбомы, первую страницу которых бот уже видел, и когда. Остальные страницы Telegram присылает отдельными
# сообщениями, иногда уже после ответа на первую: без этой памяти поздняя страница запустила бы второе
# платное распознавание и сбросила бы диалог приёмки.
ALBUM_MEMORY_SECONDS = 600
_albums: dict[str, float] = {}


def album_seen(media_group_id: str) -> bool:
    """True, если страница этого альбома уже приходила. Первую страницу запоминает."""
    now = time.monotonic()
    for old in [key for key, at in _albums.items() if now - at > ALBUM_MEMORY_SECONDS]:
        del _albums[old]
    if media_group_id in _albums:
        return True
    _albums[media_group_id] = now
    return False


class PhotoSupply(StatesGroup):
    recognizing = State()
    number = State()
    confirm = State()
    sending = State()


def pick_photo(sizes: list[PhotoSize]) -> PhotoSize | None:
    """Самый крупный вариант снимка, который не больше 1600 px по длинной стороне и не тяжелее 1 МБ.
    Telegram хранит каждое фото в нескольких размерах, так что сжимать самому не нужно."""
    fitting = [p for p in sizes if max(p.width, p.height) <= MAX_SIDE and (p.file_size or 0) <= MAX_BYTES]
    return max(fitting, key=lambda p: p.width * p.height) if fitting else None


async def fetch_file(bot: Bot, file_id: str) -> bytes:
    return (await bot.download(file_id)).getvalue()


def row_ok(row: dict) -> bool:
    return row.get("status") in ("Ok", "Warning") and bool(row.get("productId")) and bool(row.get("quantity"))


def row_problem(row: dict) -> str:
    if row.get("status") == "Error" and row.get("message"):
        return row["message"]
    if not row.get("productId"):
        return "продукт не найден в справочнике"
    if not row.get("quantity"):
        return "не указано количество"
    return row.get("message") or "ошибка в строке"


def row_line(row: dict, products: dict) -> str:
    product = products.get(row["productId"], {})
    unit = product.get("unit") or row.get("unit") or ""
    line = f"{product.get('name') or row.get('productText')} — {fmt_qty(row['quantity'], unit)}"
    norm = product.get("shelfLifeHours")
    if row.get("shelfLifeHours"):
        line += f", срок {row['shelfLifeHours']} ч"
        # Модель бывает принимает дату накладной за «годен до»: расхождение с нормативом видно сразу.
        if norm and norm != row["shelfLifeHours"]:
            line += f" (норматив {norm} ч)"
    elif norm:
        line += f", срок по нормативу ({norm} ч)"
    if row.get("cost") is not None:
        line += f", {fmt_money(row['cost'])} за {unit_label(unit)}"
        current = row.get("currentCost")
        if current is not None and round(current, 2) != round(row["cost"], 2):
            line += f" (в справочнике {fmt_money(current)}, после приёмки станет {fmt_money(row['cost'])})"
    if row.get("status") == "Warning" and row.get("message"):
        line += f"\n   Внимание: {row['message']}"
    return line


def rows_text(good: list[dict], bad: list[dict], products: dict) -> str:
    parts = [f"Распознано строк: {len(good) + len(bad)}."]
    if good:
        parts.append("Войдут в поставку:\n" + "\n".join(f"{n}. {row_line(r, products)}" for n, r in enumerate(good, 1)))
    if bad:
        lines = [f"Строка {r.get('line')}: «{r.get('productText') or '?'}» — {row_problem(r)}" for r in bad]
        parts.append("Не войдут, есть ошибки:\n" + "\n".join(lines))
    return "\n\n".join(parts)


def has_recognized_life(data: dict) -> bool:
    return any(r.get("shelfLifeHours") for r in data["good"])


def step_keyboard(data: dict, confirm_step: bool):
    buttons = []
    if confirm_step:
        buttons.append([("Принять поставку", "ph:send")])
    if has_recognized_life(data):
        buttons.append([("Сроки по нормативу", "ph:norm")])
    buttons.append(cancel_row())
    return rows(*buttons)


def number_step(data: dict) -> tuple[str, object]:
    text = rows_text(data["good"], data["bad"], data["products"])
    return text + "\n\nНомер поставки? Например, П-0413. Отменить: /cancel", step_keyboard(data, False)


def confirm_step(data: dict) -> tuple[str, object]:
    skipped = f"\nСтрок с ошибками не войдёт: {len(data['bad'])}." if data["bad"] else ""
    text = (
        f"Поставка {data['number']} по фото.\n\n"
        f"{rows_text(data['good'], data['bad'], data['products'])}{skipped}\n\nПринять поставку?"
    )
    return text, step_keyboard(data, True)


def supply_items(good: list[dict]) -> list[dict]:
    return [
        {"productId": r["productId"], "quantity": r["quantity"], "shelfLifeHours": r.get("shelfLifeHours"),
         "cost": r.get("cost")}
        for r in good
    ]


# Фото


@router.message(
    F.photo | F.document,
    StateFilter(None, SupplyForm.number, PhotoSupply.recognizing, PhotoSupply.number, PhotoSupply.confirm),
)
async def got_photo(
    message: Message, state: FSMContext, api: ApiClient, token: str, session: Session, bot: Bot,
    db: Storage, tz: ZoneInfo,
) -> None:
    chat_id = message.chat.id
    # Страницы альбома, кроме первой: и пока идёт распознавание, и когда оно уже закончилось.
    if message.media_group_id and album_seen(message.media_group_id):
        return
    if chat_id in _recognizing:
        # Остальные страницы альбома приходят сразу за первой: о них уже сказано в ответе на первую.
        if not (message.media_group_id and message.media_group_id == _recognizing[chat_id]):
            await message.answer(BUSY)
        return
    _recognizing[chat_id] = message.media_group_id
    try:
        await start_recognition(message, state, api, token, session, bot, db, tz)
    finally:
        _recognizing.pop(chat_id, None)


async def start_recognition(
    message: Message, state: FSMContext, api: ApiClient, token: str, session: Session, bot: Bot,
    db: Storage, tz: ZoneInfo,
) -> None:
    if not await access.check(message, session, "stock", "принимать поставки"):
        return
    # Прошлая поставка без ответа сервера: сказать, принята ли она, до нового платного распознавания.
    note = await unfinished_supply_note(api, db, token, message.chat.id, tz)
    if note:
        await message.answer(note.strip())
    if message.document and (message.document.mime_type or "") not in IMAGE_TYPES:
        await message.answer("Пришлите фото накладной или этикетки (JPEG, PNG или WebP).")
        return
    if message.photo:
        photo = pick_photo(message.photo)
        if photo is None:
            await message.answer(PHOTO_TOO_BIG)
            return
        file_id, too_big = photo.file_id, PHOTO_TOO_BIG
    else:
        if (message.document.file_size or 0) > MAX_BYTES:
            await message.answer(FILE_TOO_BIG)
            return
        file_id, too_big = message.document.file_id, FILE_TOO_BIG

    # Текст, присланный во время распознавания, не уйдёт в номер поставки.
    await state.clear()
    await state.set_state(PhotoSupply.recognizing)
    try:
        await recognize(message, state, api, token, bot, file_id, too_big)
    finally:
        if await state.get_state() == PhotoSupply.recognizing.state:
            await state.clear()


async def recognize(
    message: Message, state: FSMContext, api: ApiClient, token: str, bot: Bot, file_id: str, too_big: str
) -> None:
    album = (
        " Из альбома распознаю только первую страницу: остальные пришлите по одной после приёмки."
        if message.media_group_id else ""
    )
    await message.answer("Распознаю накладную, это может занять до минуты." + album)
    # Справочник до распознавания: заодно будит сервер повторяемым GET, а распознанный снимок
    # не пропадёт из-за сбоя этого запроса.
    catalog = product_cache(await api.list_products(token))
    try:
        data = await fetch_file(bot, file_id)
    except (TelegramAPIError, aiohttp.ClientError, asyncio.TimeoutError) as exc:
        # getFile aiogram оборачивает в TelegramAPIError, а сам поток файла — нет.
        log.warning("Фото %s не скачалось из Telegram: %s", file_id, exc)
        await message.answer(DOWNLOAD_FAILED)
        return
    try:
        result = await api.preview_supply_import(token, data)
    except TooManyRequests as exc:
        await message.answer(f"{exc.detail} Или {MANUAL}" if exc.detail else QUOTA)
        return
    except ApiUnavailable as exc:
        log.warning("Распознавание не удалось: %s %r", exc, exc.body)
        if exc.from_app and exc.status == 503:
            await message.answer(NOT_CONFIGURED)
        elif exc.from_app and exc.status == 502:
            await message.answer(NOT_RECOGNIZED)
        elif exc.unsure:
            await message.answer(NO_ANSWER)
        else:
            raise
        return
    except NotFound:
        await message.answer(OLD_SERVER)
        return
    except ValidationFailed as exc:
        await message.answer(api_error_text(exc, "На фото не нашлось строк с продуктами.") + "\nПришлите другое фото.")
        return
    except ApiError as exc:
        if exc.status == 413:
            await message.answer(too_big)
            return
        raise

    # Пока шло распознавание, сотрудник мог отменить его (/cancel) или начать другой диалог.
    if await state.get_state() != PhotoSupply.recognizing.state:
        log.info("Распознавание в чате %s пришло после отмены, результат не показан", message.chat.id)
        return
    good = [r for r in result if row_ok(r)]
    bad = [r for r in result if not row_ok(r)]
    products = {r["productId"]: catalog[r["productId"]] for r in good if r["productId"] in catalog}
    if not good:
        await state.clear()
        await send(message, rows_text(good, bad, products) + f"\n\nПринимать нечего. Исправьте справочник или {MANUAL}")
        return
    await state.update_data(good=good, bad=bad, products=products)
    await state.set_state(PhotoSupply.number)
    text, keyboard = number_step(await state.get_data())
    await send(message, text, keyboard)


# Номер и подтверждение


@router.message(PhotoSupply.number, ~F.text.startswith("/"))
async def got_number(message: Message, state: FSMContext) -> None:
    number = (message.text or "").strip()
    if not number or len(number) > MAX_NUMBER:
        await message.answer(f"Номер поставки — от 1 до {MAX_NUMBER} символов. Введите номер.")
        return
    if number.startswith(SYSTEM_PREFIXES):
        await message.answer("Номера на «З-», «Т-» и «Г-» заняты системой. Введите другой номер.")
        return
    data = await state.update_data(number=number)
    await state.set_state(PhotoSupply.confirm)
    text, keyboard = confirm_step(data)
    await send(message, text, keyboard)


@router.callback_query(StateFilter(PhotoSupply.number, PhotoSupply.confirm), F.data == "ph:norm")
async def cb_norm(callback: CallbackQuery, state: FSMContext) -> None:
    data = await state.get_data()
    good = [{**r, "shelfLifeHours": None} for r in data["good"]]
    data = await state.update_data(good=good)
    on_confirm = await state.get_state() == PhotoSupply.confirm.state
    text, keyboard = confirm_step(data) if on_confirm else number_step(data)
    await show(callback, text, keyboard)


@router.callback_query(PhotoSupply.confirm, F.data == "ph:send")
async def cb_send(
    callback: CallbackQuery, state: FSMContext, api: ApiClient, token: str, tz: ZoneInfo, db: Storage
) -> None:
    data = await state.get_data()
    await submit_supply(
        callback, state, api, token, tz, db,
        items=supply_items(data.get("good") or []), supplier=None,
        states=(PhotoSupply.number, PhotoSupply.confirm, PhotoSupply.sending),
        send_data="ph:send", restart_hint=f"Исправьте строки на сайте или {MANUAL}",
    )
