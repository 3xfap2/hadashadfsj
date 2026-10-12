"""Форматирование чисел, единиц и времени, разбор чисел от пользователя."""

from __future__ import annotations

import math
from datetime import datetime, timezone
from decimal import ROUND_HALF_UP, Decimal, InvalidOperation
from zoneinfo import ZoneInfo

UNIT_LABELS = {"Kg": "кг", "L": "л", "Pcs": "шт"}


class InputError(ValueError):
    """Неверный ввод пользователя. Текст исключения показывается пользователю."""


def unit_label(unit: str) -> str:
    return UNIT_LABELS.get(unit, unit)


def _group(digits: str) -> str:
    sign = "-" if digits.startswith("-") else ""
    digits = digits.lstrip("-")
    groups = []
    while len(digits) > 3:
        groups.insert(0, digits[-3:])
        digits = digits[:-3]
    groups.insert(0, digits)
    return sign + " ".join(groups)


def fmt_number(value: float, places: int = 3) -> str:
    """0.70 -> "0,7", 2.0 -> "2", 12500 -> "12 500"."""
    try:
        d = Decimal(str(value)).quantize(Decimal(1).scaleb(-places), ROUND_HALF_UP)
    except InvalidOperation:  # число длиннее 28 цифр: показать как есть, а не ронять сообщение
        return str(value)
    text = format(d.normalize(), "f")
    if text in ("-0", "0"):
        return "0"
    whole, _, frac = text.partition(".")
    return _group(whole) + ("," + frac if frac else "")


def fmt_qty(value: float, unit: str) -> str:
    return f"{fmt_number(value)} {unit_label(unit)}"


def fmt_money(value: float) -> str:
    try:
        rounded = Decimal(str(value)).quantize(Decimal(1), ROUND_HALF_UP)
    except InvalidOperation:
        return f"{value} ₽"
    return f"{_group(str(int(rounded)))} ₽"


def _duration(hours: float) -> str:
    if hours < 1:
        return f"{max(1, int(hours * 60))} мин"
    whole = int(hours)
    if whole < 48:
        return f"{whole} ч"
    days, rest = divmod(whole, 24)
    return f"{days} сут" + (f" {rest} ч" if rest else "")


def plural(n: int, one: str, few: str, many: str) -> str:
    """plural(1, "порцию", "порции", "порций") -> "порцию"; 3 -> "порции"; 8 -> "порций"."""
    n = abs(int(n))
    if n % 10 == 1 and n % 100 != 11:
        return one
    if 2 <= n % 10 <= 4 and not 12 <= n % 100 <= 14:
        return few
    return many


def fmt_hours_left(hours: float) -> str:
    """5 -> "осталось 5 ч", -3 -> "просрочено 3 ч назад"."""
    if hours == 0:
        return "срок истекает сейчас"
    if hours > 0:
        return f"осталось {_duration(hours)}"
    return f"просрочено {_duration(-hours)} назад"


def parse_dt(value: str) -> datetime:
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt


def fmt_dt(value: str | datetime, tz: ZoneInfo) -> str:
    dt = parse_dt(value) if isinstance(value, str) else value
    return dt.astimezone(tz).strftime("%d.%m %H:%M")


MAX_QUANTITY = 1_000_000   # предел сервера на количество в строке поставки
MAX_MONEY = 10_000_000


def parse_number(text: str | None, *, limit: float = MAX_QUANTITY) -> float:
    """Число от пользователя: принимает и запятую, и точку. Запись вида 1e3 и 1_000 не принимается,
    числа больше limit по модулю — тоже: их не примет сервер и не покажет форматирование."""
    raw = (text or "").strip()
    for space in (" ", " ", " "):
        raw = raw.replace(space, "")
    if not raw:
        raise InputError("Нужно число, например 2,5.")
    if raw.count(",") + raw.count(".") > 1 or "_" in raw or "e" in raw.lower():
        raise InputError(f"«{text.strip()}» не похоже на число. Пример: 2,5.")
    try:
        value = float(raw.replace(",", "."))
    except ValueError:
        raise InputError(f"«{text.strip()}» не похоже на число. Пример: 2,5.") from None
    if not math.isfinite(value):
        raise InputError("Нужно обычное число, например 2,5.")
    if abs(value) > limit:
        raise InputError(f"Слишком большое число: не больше {fmt_number(limit)}.")
    return value


def parse_positive(text: str | None, *, integer: bool = False) -> float | int:
    value = parse_number(text)
    if value <= 0:
        raise InputError("Число должно быть больше нуля.")
    if integer:
        if value != int(value):
            raise InputError("Нужно целое число, без дробной части.")
        return int(value)
    return value


def parse_money(text: str | None) -> float:
    value = parse_number(text, limit=MAX_MONEY)
    if value < 0:
        raise InputError("Сумма не может быть отрицательной.")
    return value


def plain(value: float) -> float | int:
    """Число для JSON: 2.0 -> 2, 2.5 -> 2.5."""
    return int(value) if float(value).is_integer() else value
