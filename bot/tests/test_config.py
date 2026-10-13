import socket
from pathlib import Path

import httpx
import pytest

from ostatok_bot.app import start_health_server
from ostatok_bot.config import Config

VARS = ["BOT_TOKEN", "API_URL", "POLL_SECONDS", "DATA_DIR", "TIMEZONE", "PORT", "WEBAPP_URL"]


@pytest.fixture
def env(monkeypatch):
    for name in VARS:
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("BOT_TOKEN", "1:abc")
    return monkeypatch


def test_defaults(env):
    config = Config.from_env()
    assert config.api_url == "http://localhost:5078"
    assert config.poll_seconds == 30
    assert config.db_path == Path("data") / "bot.sqlite3"
    assert config.tz.key == "Europe/Moscow"
    assert config.port is None


def test_webapp_url_follows_https_api_or_is_off(env):
    assert Config.from_env().webapp_url is None  # http://localhost — Telegram мини-приложение не откроет
    env.setenv("API_URL", "https://ostatok.onrender.com/")
    assert Config.from_env().webapp_url == "https://ostatok.onrender.com/app"
    env.setenv("WEBAPP_URL", "https://example.ru/app")
    assert Config.from_env().webapp_url == "https://example.ru/app"


def test_all_settings_from_env(env, tmp_path):
    env.setenv("API_URL", "https://api.example.ru")
    env.setenv("POLL_SECONDS", "15")
    env.setenv("DATA_DIR", str(tmp_path / "volume"))
    env.setenv("TIMEZONE", "Asia/Yekaterinburg")
    env.setenv("PORT", "10000")
    config = Config.from_env()
    assert config.api_url == "https://api.example.ru"
    assert config.poll_seconds == 15
    assert config.db_path == tmp_path / "volume" / "bot.sqlite3"
    assert config.tz.key == "Asia/Yekaterinburg"
    assert config.port == 10000


def test_empty_values_mean_default(env):
    # на хостингах переменную часто заводят пустой
    env.setenv("API_URL", "")
    env.setenv("POLL_SECONDS", "")
    env.setenv("PORT", " ")
    config = Config.from_env()
    assert config.api_url == "http://localhost:5078"
    assert config.poll_seconds == 30
    assert config.port is None


@pytest.mark.parametrize(
    "name, value, message",
    [
        ("BOT_TOKEN", "", "Не задан BOT_TOKEN"),
        ("POLL_SECONDS", "полминуты", "POLL_SECONDS должно быть числом"),
        ("POLL_SECONDS", "0", "POLL_SECONDS должно быть больше нуля"),
        ("PORT", "80.5", "PORT должно быть числом"),
        ("TIMEZONE", "Moscow", "Неизвестный часовой пояс"),
    ],
)
def test_bad_values_stop_with_clear_message(env, name, value, message):
    env.setenv(name, value)
    with pytest.raises(SystemExit, match=message):
        Config.from_env()


async def test_health_server():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    runner = await start_health_server(port)
    try:
        # trust_env=False: HTTP_PROXY из окружения не должен перехватывать запрос к 127.0.0.1
        async with httpx.AsyncClient(trust_env=False) as client:
            resp = await client.get(f"http://127.0.0.1:{port}/health")
        assert (resp.status_code, resp.text) == (200, "ok")
    finally:
        await runner.cleanup()
