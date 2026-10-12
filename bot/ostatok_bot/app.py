"""Сборка бота и запуск long polling."""

from __future__ import annotations

import asyncio
import logging
import os
import sqlite3
from pathlib import Path
from typing import IO
from urllib.request import getproxies

from aiogram import Bot, Dispatcher
from aiogram.client.session.aiohttp import AiohttpSession
from aiohttp import web
from aiogram.exceptions import TelegramUnauthorizedError
from aiogram.fsm.storage.memory import MemoryStorage

from .access import GUEST_COMMANDS
from .api import ApiClient
from .config import Config, load_dotenv
from .handlers import build_router
from .middleware import AuthMiddleware
from .monitor import Monitor
from .storage import Storage

log = logging.getLogger(__name__)



def build_dispatcher(api: ApiClient, db: Storage, config: Config) -> Dispatcher:
    # site_url — адрес сайта для ссылок (меню для гостей): сайт и API живут на одном адресе.
    dp = Dispatcher(storage=MemoryStorage(), api=api, db=db, tz=config.tz, site_url=config.api_url.rstrip("/"))
    auth = AuthMiddleware()
    dp.message.outer_middleware(auth)
    dp.callback_query.outer_middleware(auth)
    dp.include_router(build_router())
    return dp


async def start_health_server(port: int) -> web.AppRunner:
    """GET / и /health отвечают «ok». Нужен хостингам, которые ждут открытый HTTP-порт."""

    async def health(_request: web.Request) -> web.Response:
        return web.Response(text="ok")

    app = web.Application()
    app.router.add_get("/", health)
    app.router.add_get("/health", health)
    runner = web.AppRunner(app)
    await runner.setup()
    await web.TCPSite(runner, "0.0.0.0", port).start()
    log.info("Проверка здоровья: http://0.0.0.0:%s/health", port)
    return runner


LOCK_FILE = "bot.lock"


def lock_instance(data_dir: Path) -> IO:
    """Занять файл-блокировку в каталоге данных. Второй экземпляр бота с тем же каталогом не стартует:
    два опроса Telegram одним токеном мешают друг другу (ошибка 409), а уведомления приходят дважды.
    Блокировка снимается сама, когда процесс завершается."""
    data_dir.mkdir(parents=True, exist_ok=True)
    handle = open(data_dir / LOCK_FILE, "a+")
    try:
        if os.name == "nt":
            import msvcrt

            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl

            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        handle.close()
        raise SystemExit(
            f"Бот уже запущен на этом компьютере (занят файл {data_dir / LOCK_FILE}). "
            "Закройте другой экземпляр и запустите снова."
        ) from None
    return handle


async def main() -> None:
    load_dotenv()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    config = Config.from_env()
    lock = lock_instance(config.data_dir)  # держим файл открытым до конца работы
    try:
        db = Storage(config.db_path)
    except sqlite3.Error as exc:
        raise SystemExit(
            f"База бота {config.db_path} не открывается ({exc}). Удалите или переименуйте файл: "
            "бот создаст новый, сотрудникам придётся войти заново."
        ) from None
    api = ApiClient(config.api_url)
    # Системный прокси (HTTPS_PROXY или настройки Windows): httpx берёт его сам, aiogram — нет.
    # Там, где Telegram доступен только через прокси, без этого бот не стартует.
    proxy = getproxies().get("https")
    bot = Bot(config.bot_token, session=AiohttpSession(proxy=proxy) if proxy else None)
    dp = build_dispatcher(api, db, config)

    monitor_task = None
    health = None
    try:
        try:
            await bot.set_my_commands(GUEST_COMMANDS)
        except TelegramUnauthorizedError:
            raise SystemExit("Telegram не принял BOT_TOKEN. Проверьте токен у @BotFather.") from None

        monitor_task = asyncio.create_task(Monitor(api, db, bot, config.poll_seconds).run())

        if config.port:
            health = await start_health_server(config.port)

        log.info("Бот запущен, API: %s, данные: %s", config.api_url, config.data_dir.resolve())
        await dp.start_polling(bot)
    finally:
        if monitor_task:
            monitor_task.cancel()
            await asyncio.gather(monitor_task, return_exceptions=True)  # дождаться, прежде чем закрывать базу
        if health:
            await health.cleanup()
        await api.close()
        await bot.session.close()
        db.close()
        lock.close()


def run() -> None:
    asyncio.run(main())
