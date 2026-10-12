"""Настройки из переменных окружения."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

DB_FILE = "bot.sqlite3"


def load_dotenv(path: str | Path = ".env") -> None:
    """Подхватить .env для локального запуска. Уже заданные переменные не трогает."""
    path = Path(path)
    if not path.is_file():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        os.environ.setdefault(key.strip(), value.strip().strip('"').strip("'"))


def _env(name: str, default: str | None = None) -> str | None:
    value = os.environ.get(name, "").strip()
    return value or default


def _number(name: str, default: str, *, integer: bool = False) -> float | int:
    raw = _env(name, default)
    try:
        value = int(raw) if integer else float(raw)
    except ValueError:
        raise SystemExit(f"{name} должно быть числом, сейчас: {raw!r}") from None
    if value <= 0:
        raise SystemExit(f"{name} должно быть больше нуля, сейчас: {raw!r}")
    return value


@dataclass(frozen=True)
class Config:
    bot_token: str
    api_url: str
    poll_seconds: float
    data_dir: Path
    tz: ZoneInfo
    port: int | None = None

    @property
    def db_path(self) -> Path:
        return self.data_dir / DB_FILE

    @classmethod
    def from_env(cls) -> "Config":
        token = _env("BOT_TOKEN")
        if not token:
            raise SystemExit("Не задан BOT_TOKEN. Пример настроек в .env.example")
        timezone = _env("TIMEZONE", "Europe/Moscow")
        try:
            tz = ZoneInfo(timezone)
        except (ZoneInfoNotFoundError, ValueError):
            raise SystemExit(f"Неизвестный часовой пояс TIMEZONE={timezone!r}, пример: Europe/Moscow") from None
        return cls(
            bot_token=token,
            api_url=_env("API_URL", "http://localhost:5078"),
            poll_seconds=_number("POLL_SECONDS", "30"),
            data_dir=Path(_env("DATA_DIR", "data")),
            tz=tz,
            port=_number("PORT", "0", integer=True) if _env("PORT") else None,
        )
