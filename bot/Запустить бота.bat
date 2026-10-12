@echo off
chcp 65001 >nul
title Bot Ostatok
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    echo Первый запуск: создаю окружение и ставлю зависимости, это пара минут.
    py -3.12 -m venv .venv || goto :end
    .venv\Scripts\python -m pip install -q -r requirements.txt || goto :end
)
echo Запуск бота. Не закрывайте это окно, пока бот нужен. Остановить: Ctrl+C
echo.
.venv\Scripts\python -m ostatok_bot
:end
echo.
echo Бот остановлен. Если выше ошибка, пришлите её скриншот.
pause
