"""SQLite бота: сессии вошедших чатов и последнее состояние для уведомлений по ресторанам.
Данных ресторана здесь нет, пароли не хранятся. Неудачные входы считает сервер."""

from __future__ import annotations

import json
import sqlite3
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True)
class Session:
    """Вошедший чат: токен и то, что о сотруднике сказал GET /api/me при входе.
    Роль и ресторан меняются только вместе с токеном: после смены роли сервер отзывает старый токен."""

    chat_id: int
    token: str
    expires_at: datetime
    login: str = ""
    title: str = ""
    role: str = ""
    sections: tuple[str, ...] = field(default_factory=tuple)
    restaurant_id: str | None = None
    restaurant: str = ""

    def is_valid(self, now: datetime) -> bool:
        return self.expires_at > now

    def allows(self, section: str) -> bool:
        return section in self.sections


class Storage:
    def __init__(self, path: str | Path):
        if str(path) != ":memory:":
            Path(path).parent.mkdir(parents=True, exist_ok=True)
        self._db = sqlite3.connect(str(path))
        self._db.executescript(
            """
            -- Сессии прежней версии не знали роли и ресторана: таким чатам нужно войти заново.
            DROP TABLE IF EXISTS sessions;
            CREATE TABLE IF NOT EXISTS chat_sessions (
                chat_id INTEGER PRIMARY KEY,
                token TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                login TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                role TEXT NOT NULL DEFAULT '',
                sections TEXT NOT NULL DEFAULT '[]',
                restaurant_id TEXT,
                restaurant TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS monitor_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pending_ops (
                chat_id INTEGER NOT NULL,
                kind TEXT NOT NULL,
                op_id TEXT NOT NULL,
                data TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (chat_id, kind)
            );
            """
        )
        self._db.commit()

    def close(self) -> None:
        self._db.close()

    # Сессии

    def save_session(
        self,
        chat_id: int,
        token: str,
        expires_at: datetime,
        *,
        login: str = "",
        title: str = "",
        role: str = "",
        sections: tuple[str, ...] | list[str] = (),
        restaurant_id: str | None = None,
        restaurant: str = "",
    ) -> None:
        self._db.execute(
            "INSERT OR REPLACE INTO chat_sessions "
            "(chat_id, token, expires_at, login, title, role, sections, restaurant_id, restaurant) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
            (chat_id, token, expires_at.isoformat(), login, title, role,
             json.dumps(list(sections)), restaurant_id, restaurant),
        )
        self._db.commit()

    def get_session(self, chat_id: int) -> Session | None:
        row = self._db.execute(
            "SELECT chat_id, token, expires_at, login, title, role, sections, restaurant_id, restaurant "
            "FROM chat_sessions WHERE chat_id = ?",
            (chat_id,),
        ).fetchone()
        return _session(row) if row else None

    def delete_session(self, chat_id: int, token: str | None = None) -> bool:
        """Удалить сессию чата. С token — только если в базе всё ещё этот токен: пока шёл запрос
        со старым токеном, чат мог войти заново, и новую сессию стирать нельзя. True — удалена."""
        if token is None:
            cursor = self._db.execute("DELETE FROM chat_sessions WHERE chat_id = ?", (chat_id,))
        else:
            cursor = self._db.execute("DELETE FROM chat_sessions WHERE chat_id = ? AND token = ?", (chat_id, token))
        self._db.commit()
        return cursor.rowcount > 0

    def active_sessions(self, now: datetime) -> list[Session]:
        rows = self._db.execute(
            "SELECT chat_id, token, expires_at, login, title, role, sections, restaurant_id, restaurant "
            "FROM chat_sessions ORDER BY chat_id"
        ).fetchall()
        return [s for s in map(_session, rows) if s.is_valid(now)]

    def active_chats(self, now: datetime) -> list[int]:
        return [s.chat_id for s in self.active_sessions(now)]

    # Состояние для уведомлений

    def load_state(self, key: str) -> Any | None:
        row = self._db.execute("SELECT value FROM monitor_state WHERE key = ?", (key,)).fetchone()
        return json.loads(row[0]) if row else None

    def save_state(self, key: str, value: Any) -> None:
        self._db.execute(
            "INSERT INTO monitor_state (key, value) VALUES (?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            (key, json.dumps(value, ensure_ascii=False)),
        )
        self._db.commit()

    # Незавершённые отправки: заказ или поставка ушли на сервер, а ответ бот не получил (или его перезапустили).

    def save_pending(self, chat_id: int, kind: str, op_id: str, data: Any) -> None:
        self._db.execute(
            "INSERT OR REPLACE INTO pending_ops (chat_id, kind, op_id, data, created_at) VALUES (?, ?, ?, ?, ?)",
            (chat_id, kind, op_id, json.dumps(data, ensure_ascii=False), utcnow().isoformat()),
        )
        self._db.commit()

    def get_pending(self, chat_id: int, kind: str) -> dict | None:
        """{"id", "data", "at"} незавершённой отправки этого вида или None."""
        row = self._db.execute(
            "SELECT op_id, data, created_at FROM pending_ops WHERE chat_id = ? AND kind = ?", (chat_id, kind)
        ).fetchone()
        return {"id": row[0], "data": json.loads(row[1]), "at": datetime.fromisoformat(row[2])} if row else None

    def delete_pending(self, chat_id: int, kind: str) -> None:
        self._db.execute("DELETE FROM pending_ops WHERE chat_id = ? AND kind = ?", (chat_id, kind))
        self._db.commit()


def _session(row) -> Session:
    chat_id, token, expires, login, title, role, sections, restaurant_id, restaurant = row
    return Session(
        chat_id, token, datetime.fromisoformat(expires), login, title, role,
        tuple(json.loads(sections)), restaurant_id, restaurant,
    )
