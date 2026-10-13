"""Клиент HTTP API «Остатка». Весь доступ бота к API идёт только через этот модуль.

Каждое действие — отдельный метод ApiClient. Первый аргумент — токен пользователя,
от имени которого выполняется действие: сервер берёт из токена ресторан и роль, поэтому
данные и права у каждого сотрудника свои. Методы возвращают JSON ответа (dict/list)
или бросают исключение из иерархии ApiError. Эти же методы будет вызывать ИИ-ассистент.
"""

from __future__ import annotations

import asyncio
import base64
import json
import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Literal, NotRequired, TypedDict

import httpx

from .fmt import parse_dt, plain

log = logging.getLogger(__name__)

Unit = Literal["Kg", "L", "Pcs"]
WriteOffReason = Literal["Spoiled", "Defective"]


class RecipeItem(TypedDict):
    productId: str
    quantity: float


class SupplyItemIn(TypedDict):
    productId: str
    quantity: float
    shelfLifeHours: NotRequired[int | None]
    # Цена за единицу из накладной: после приёмки она станет закупочной ценой продукта.
    cost: NotRequired[float | None]


class InventoryItemIn(TypedDict):
    productId: str
    actual: float


class OrderItemIn(TypedDict):
    dishId: str
    quantity: int
    # Попросить скидку горячего предложения; сервер применит её, только если она ещё действует.
    discount: NotRequired[bool]


# --- ошибки ---------------------------------------------------------------------


class ApiError(Exception):
    def __init__(self, status: int, body: Any = None):
        self.status = status
        self.body = body
        # Токен запроса, на который пришла ошибка: по нему видно, чью сессию она касается.
        self.token: str | None = None
        super().__init__(f"API ответил {status}")

    @property
    def errors(self) -> dict[str, list[str]]:
        if isinstance(self.body, dict) and isinstance(self.body.get("errors"), dict):
            return self.body["errors"]
        return {}

    def extension(self, name: str) -> Any:
        """Дополнительное поле ответа с ошибкой (ProblemDetails кладёт их в корень)."""
        return self.body.get(name) if isinstance(self.body, dict) else None

    def messages(self) -> list[str]:
        """Тексты ошибок проверки из поля errors, без имён полей."""
        result = []
        for texts in self.errors.values():
            if isinstance(texts, str):
                texts = [texts]
            result.extend(str(t) for t in texts)
        return result

    @property
    def detail(self) -> str | None:
        """Текст ошибки, который сервер положил в detail (409, 404 и другие ответы без errors)."""
        value = self.extension("detail")
        return value.strip() if isinstance(value, str) and value.strip() else None


class ValidationFailed(ApiError):
    """400: данные не прошли проверку, тексты в messages()."""


class Unauthorized(ApiError):
    """401: токен истёк или отозван (сотрудника удалили, сменили роль или пароль), либо неверный пароль."""

    @property
    def attempts_left(self) -> int | None:
        """При входе: сколько попыток осталось до блокировки логина."""
        value = self.extension("attemptsLeft")
        return value if isinstance(value, int) else None


class Forbidden(ApiError):
    """403: роли сотрудника это действие недоступно."""


class NotFound(ApiError):
    """404."""


class Conflict(ApiError):
    """409."""


class TooManyRequests(ApiError):
    """429: вход для логина заблокирован после неверных паролей или слишком много запросов с адреса."""

    @property
    def retry_after_minutes(self) -> int | None:
        """Через сколько минут вход откроется. Нет, если сработал общий предел запросов."""
        value = self.extension("retryAfterMinutes")
        return value if isinstance(value, int) else None


class ApiUnavailable(ApiError):
    """API не отвечает или вернул 5xx. unsure — изменяющий запрос мог дойти до сервера, но ответа не было:
    неизвестно, выполнил ли его сервер. from_app — 5xx сформировало само приложение (ProblemDetails),
    а не прокси хостинга: например, 502 «фото не распозналось» или 503 «распознавание не настроено»."""

    def __init__(
        self, status: int = 0, body: Any = None, reason: str = "", unsure: bool = False, from_app: bool = False
    ):
        super().__init__(status, body)
        self.reason = reason or f"код {status}"
        self.unsure = unsure
        self.from_app = from_app
        self.args = (f"API недоступен: {self.reason}",)


_ERRORS_BY_STATUS = {
    400: ValidationFailed,
    401: Unauthorized,
    403: Forbidden,
    404: NotFound,
    409: Conflict,
    429: TooManyRequests,
}


def _from_app(resp: httpx.Response) -> bool:
    """Ответ сформировало само приложение (ProblemDetails), а не прокси хостинга, пока API просыпается."""
    return resp.headers.get("content-type", "").startswith("application/problem+json")


def _created(result: Any, status: int = 200) -> dict:
    """Ответ на создание заказа или поставки. Успешный статус без JSON-объекта (страница прокси, пустое тело)
    не говорит, выполнен ли запрос: это «результат неизвестен», а не успех."""
    if not isinstance(result, dict):
        raise ApiUnavailable(status, result, reason="успешный статус без JSON в ответе", unsure=True)
    return result


def _describe(failure: Exception | httpx.Response) -> str:
    return f"код {failure.status_code}" if isinstance(failure, httpx.Response) else type(failure).__name__


@dataclass(frozen=True)
class LoginResult:
    token: str
    expires_at: datetime


def restaurant_of(token: str) -> str | None:
    """Идентификатор ресторана из токена (поле restaurant). Подпись не проверяется: это делает сервер,
    а боту ресторан нужен только чтобы не смешивать уведомления разных ресторанов."""
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        value = json.loads(base64.urlsafe_b64decode(payload)).get("restaurant")
    except (IndexError, ValueError, AttributeError):
        return None
    return value if isinstance(value, str) else None


# --- клиент ---------------------------------------------------------------------


# Бесплатный сервер API засыпает и после простоя отвечает до минуты: ждём ответа дольше обычного,
# а на 502–504 от прокси и обрыв соединения повторяем запрос.
TIMEOUT = httpx.Timeout(70, connect=20)
RETRY_STATUSES = (502, 503, 504)
# 504 значит, что прокси передал запрос, но не дождался ответа: изменение могло выполниться.
UNSAFE_RETRY_STATUSES = (502, 503)
RETRY_DELAYS = (3, 10)


class ApiClient:
    def __init__(
        self,
        base_url: str,
        *,
        http: httpx.AsyncClient | None = None,
        timeout: httpx.Timeout | float = TIMEOUT,
        retry_delays: tuple[float, ...] = RETRY_DELAYS,
    ):
        self._http = http or httpx.AsyncClient(base_url=base_url.rstrip("/"), timeout=timeout)
        self._retry_delays = retry_delays

    async def close(self) -> None:
        await self._http.aclose()

    async def _send(
        self, method: str, path: str, idempotent: bool = False, **kwargs: Any
    ) -> tuple[httpx.Response, bool]:
        """Запрос с повторами. Возвращает ответ и признак «запрос мог дойти до приложения раньше».

        GET и идемпотентные запросы (повтор с тем же id вернёт прежний результат) повторяются на любой
        таймаут, обрыв и 502–504 от прокси. Остальные изменяющие — только если до приложения они не дошли:
        соединение не установилось или прокси ответил 502/503. На таймаут и 504 они не повторяются:
        поставка могла пройти, и повтор задвоил бы её.
        Ответ самого приложения (application/problem+json) не повторяется никогда: 502 «фото не
        распозналось» или 503 «распознавание не настроено» повтор не исправит, а снимок из лимита израсходует."""
        safe = method == "GET" or idempotent
        retry_statuses = RETRY_STATUSES if safe else UNSAFE_RETRY_STATUSES
        maybe_delivered = False
        for delay in (*self._retry_delays, None):
            try:
                resp = await self._http.request(method, path, **kwargs)
            except (httpx.ConnectError, httpx.ConnectTimeout) as exc:
                failure: Exception | httpx.Response = exc
            except httpx.HTTPError as exc:
                if not safe:
                    raise ApiUnavailable(reason=f"{type(exc).__name__}: {exc}", unsure=True) from exc
                maybe_delivered = True
                failure = exc
            else:
                if resp.status_code not in retry_statuses or _from_app(resp):
                    return resp, maybe_delivered
                maybe_delivered = maybe_delivered or resp.status_code == 504
                failure = resp
            if delay is None:
                break
            log.info("API не ответил (%s), повтор через %s с", _describe(failure), delay)
            await asyncio.sleep(delay)
        if isinstance(failure, httpx.Response):
            return failure, maybe_delivered
        raise ApiUnavailable(
            reason=f"{type(failure).__name__}: {failure}", unsure=method != "GET" and maybe_delivered
        ) from failure

    async def _request(
        self,
        method: str,
        path: str,
        token: str | None = None,
        json: Any = None,
        ok: tuple[int, ...] = (200,),
        params: dict[str, Any] | None = None,
        content: bytes | None = None,
        idempotent: bool = False,
    ) -> Any:
        headers = {"Authorization": f"Bearer {token}"} if token else {}
        if content is not None:
            headers["Content-Type"] = "application/octet-stream"
            resp, maybe_delivered = await self._send(
                method, path, idempotent, content=content, headers=headers, params=params
            )
        else:
            resp, maybe_delivered = await self._send(method, path, idempotent, json=json, headers=headers, params=params)
        body = None
        if resp.content:
            try:
                body = resp.json()
            except ValueError:
                body = resp.text
        if resp.status_code in ok:
            return body
        if resp.status_code >= 500:
            from_app = _from_app(resp)
            # Прежняя попытка могла дойти (таймаут, 504), даже если последний ответ — ошибка приложения.
            unsure = method != "GET" and (maybe_delivered or (not from_app and resp.status_code == 504))
            raise ApiUnavailable(resp.status_code, body, unsure=unsure, from_app=from_app)
        error = _ERRORS_BY_STATUS.get(resp.status_code, ApiError)(resp.status_code, body)
        error.token = token
        raise error

    async def _with_result_on_conflict(
        self, method: str, path: str, token: str, body: Any, marker: str, idempotent: bool = False
    ) -> dict:
        """POST, у которого и 201, и 409 несут результат (заказ отклонён, на заготовку не хватило сырья)."""
        try:
            return await self._request(method, path, token, body, ok=(200, 201), idempotent=idempotent)
        except Conflict as exc:
            if isinstance(exc.body, dict) and marker in exc.body:
                return exc.body
            raise

    # Вход

    async def login(self, login: str, password: str) -> LoginResult:
        """POST /api/auth/login. 401 — неверный логин или пароль (attemptsLeft), 429 — вход закрыт (retryAfterMinutes)."""
        data = await self._request("POST", "/api/auth/login", json={"login": login, "password": password})
        return LoginResult(token=data["token"], expires_at=parse_dt(data["expiresAt"]))

    async def get_me(self, token: str) -> dict:
        """Кто вошёл: login, title, role, sections (разделы, доступные роли), shift, places, restaurant, restaurantId."""
        return await self._request("GET", "/api/me", token)

    # Продукты

    async def list_products(self, token: str) -> list[dict]:
        return await self._request("GET", "/api/products", token)

    async def create_product(self, token: str, name: str, unit: Unit, shelf_life_hours: int, cost: float) -> dict:
        """201 — создан; 409 — название занято; 400 — ошибки проверки; 403 — нужна роль управляющего или шефа."""
        body = _product_body(name, unit, shelf_life_hours, cost, None)
        return await self._request("POST", "/api/products", token, body, ok=(200, 201))

    async def update_product(
        self,
        token: str,
        product_id: str,
        name: str,
        unit: Unit,
        shelf_life_hours: int,
        cost: float,
        prep_recipe: list[RecipeItem] | None = None,
        opened_shelf_life_hours: int | None = None,
    ) -> dict:
        """200 — изменён; 404; 409 — название занято или нельзя сменить единицу (продукт уже в поставках
        или техкартах), текст в detail. prep_recipe и opened_shelf_life_hours нужно передать прежние:
        без них API сотрёт состав заготовки и срок после вскрытия."""
        body = _product_body(name, unit, shelf_life_hours, cost, prep_recipe)
        if opened_shelf_life_hours is not None:
            body["openedShelfLifeHours"] = opened_shelf_life_hours
        return await self._request("PUT", f"/api/products/{product_id}", token, body)

    async def delete_product(self, token: str, product_id: str) -> None:
        """204 — удалён; 404; 409 — продукт используется в поставках, техкартах или заготовках."""
        await self._request("DELETE", f"/api/products/{product_id}", token, ok=(200, 204))

    # Блюда и техкарты

    async def list_dishes(self, token: str) -> list[dict]:
        return await self._request("GET", "/api/dishes", token)

    async def create_dish(
        self,
        token: str,
        name: str,
        price: float,
        recipe: list[RecipeItem],
        photo_url: str | None = None,
        category: str | None = None,
    ) -> dict:
        """201 — создано; 409 — название занято."""
        body = _dish_body(name, price, recipe, photo_url, category)
        return await self._request("POST", "/api/dishes", token, body, ok=(200, 201))

    async def update_dish(
        self,
        token: str,
        dish_id: str,
        name: str,
        price: float,
        recipe: list[RecipeItem],
        photo_url: str | None = None,
        category: str | None = None,
    ) -> dict:
        """Состав заменяется целиком. 200; 404; 409 — название занято.
        category нужно передать прежнюю: без неё API уберёт блюдо из раздела меню."""
        body = _dish_body(name, price, recipe, photo_url, category)
        return await self._request("PUT", f"/api/dishes/{dish_id}", token, body)

    async def delete_dish(self, token: str, dish_id: str) -> None:
        """204; 404; 409 — по блюду уже есть заказы."""
        await self._request("DELETE", f"/api/dishes/{dish_id}", token, ok=(200, 204))

    async def stop_dish(self, token: str, dish_id: str) -> None:
        """Снять блюдо с продажи вручную. 204; 404; 403 — нужна роль кухни (управляющий, шеф, повар)."""
        await self._request("POST", f"/api/dishes/{dish_id}/stop", token, ok=(200, 204))

    async def resume_dish(self, token: str, dish_id: str) -> None:
        """Вернуть блюдо в продажу. 204; 404; 403."""
        await self._request("POST", f"/api/dishes/{dish_id}/resume", token, ok=(200, 204))

    # Поставки

    async def list_supplies(self, token: str, take: int = 50) -> list[dict]:
        return await self._request("GET", "/api/supplies", token, params={"take": take})

    async def create_supply(
        self,
        token: str,
        number: str,
        items: list[SupplyItemIn],
        supplier: str | None = None,
        supply_id: str | None = None,
    ) -> dict:
        """Принять поставку. Без shelfLifeHours берётся норматив продукта; cost у строки обновит закупочную цену.
        supply_id — GUID попытки: если поставка с ним уже принята (ответ на прошлую попытку не дошёл),
        сервер ответит 409, и по list_supplies видно, что она наша.
        201 — принята, в ответе id, партии со сроками и total; 409 — номер или id уже заняты."""
        payload = []
        for item in items:
            row: dict[str, Any] = {"productId": item["productId"], "quantity": plain(item["quantity"])}
            if item.get("shelfLifeHours") is not None:
                row["shelfLifeHours"] = item["shelfLifeHours"]
            if item.get("cost") is not None:
                row["cost"] = plain(item["cost"])
            payload.append(row)
        body: dict[str, Any] = {"number": number, "items": payload}
        if supplier:
            body["supplier"] = supplier
        if supply_id:
            body["id"] = supply_id
        return _created(await self._request("POST", "/api/supplies", token, body, ok=(200, 201)))

    async def preview_supply_import(self, token: str, data: bytes) -> list[dict]:
        """Распознать фото накладной или этикетки (JPEG, PNG, WebP) либо файл Excel/CSV. Ничего не сохраняет.
        200 — строки: line, productText, productId, quantity, unit, shelfLifeHours, price, status (Ok/Warning/Error),
        message, cost (цена за единицу), currentCost (цена в справочнике сейчас).
        400 — строк не нашлось; 413 — файл больше 1 МБ; 429 — больше 20 снимков в час на ресторан;
        502 — фото не распозналось (ApiUnavailable со статусом 502); 503 — распознавание на сервере не настроено.
        Строки без ошибок затем отправляются обычным create_supply."""
        return await self._request("POST", "/api/supplies/import/preview", token, content=data)

    async def list_incoming(self, token: str) -> list[dict]:
        """Поставки от внешней системы, которые ждут подтверждения: id, number, supplier, receivedAt, createdAt, total,
        items (productName, unit, quantity, shelfLifeHours). На склад они попадают только после приёмки."""
        return await self._request("GET", "/api/supplies/incoming", token)

    async def accept_incoming(self, token: str, incoming_id: str) -> dict:
        """Принять поставку: партии появляются на складе, срок годности считается от даты поставки (createdAt).
        200 — поставка с партиями; 404 — уже решена или не найдена; 409 — поставка с таким номером уже принята
        или срок годности истёк, пока она ждала (текст в detail: такую остаётся отклонить)."""
        return await self._request("POST", f"/api/supplies/incoming/{incoming_id}/accept", token)

    async def reject_incoming(self, token: str, incoming_id: str) -> None:
        """Отклонить поставку. 204; 404 — уже решена или не найдена."""
        await self._request("POST", f"/api/supplies/incoming/{incoming_id}/reject", token, ok=(200, 204))

    # Заказы

    async def get_orders_summary(self, token: str, since: datetime | None = None) -> dict:
        """Итоги с since (по умолчанию за сутки): orders, rejected, revenue и tables — столы с открытыми
        заказами (place, total, last). Время передаётся в UTC (прежние версии сервера отвечали 500 на смещение)."""
        params = {"since": since.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")} if since else None
        return await self._request("GET", "/api/orders/summary", token, params=params)

    async def list_orders(self, token: str, take: int = 50) -> list[dict]:
        """Заказы, новые сверху. Открытые — status Accepted и closedAt null."""
        return await self._request("GET", "/api/orders", token, params={"take": take})

    async def create_order(
        self, token: str, items: list[OrderItemIn], place: str | None = None, order_id: str | None = None
    ) -> dict:
        """Создать заказ (только роль официанта, иначе 403). place — место из GET /api/me, необязательно.
        order_id — GUID, который придумывает клиент на каждую попытку заказа: повтор с тем же id вернёт прежний
        заказ и ничего не спишет, поэтому с ним запрос повторяется и при таймауте.
        Возвращает заказ и при 201 (status Accepted, в writeOffs — из каких партий списано,
        у позиций discountPercent и discountQuantity), и при 409 (status Rejected, в shortages — чего не хватило)."""
        lines = []
        for i in items:
            line: dict[str, Any] = {"dishId": i["dishId"], "quantity": i["quantity"]}
            if i.get("discount"):
                line["discount"] = True
            lines.append(line)
        body: dict[str, Any] = {"items": lines}
        if place:
            body["place"] = place
        if order_id:
            body["id"] = order_id
        return _created(await self._with_result_on_conflict(
            "POST", "/api/orders", token, body, "status", idempotent=order_id is not None
        ))

    async def close_table(self, token: str, place: str) -> dict:
        """Закрыть стол: открытые заказы места уходят в историю. 200 {place, orders, total};
        404 — у места нет открытых заказов; 403 — только официант."""
        return await self._request("POST", "/api/orders/close", token, {"place": place})

    # Склад

    async def get_stock(self, token: str) -> list[dict]:
        return await self._request("GET", "/api/stock", token)

    async def get_batches(self, token: str) -> list[dict]:
        """Партии с остатком по возрастанию срока годности."""
        return await self._request("GET", "/api/stock/batches", token)

    async def write_off_batch(self, token: str, batch_id: str, reason: WriteOffReason | None = None) -> None:
        """204 — списано; 404 — нет партии; 409 — в партии уже ничего нет.
        Просроченная списывается без причины; для годной нужна reason, иначе 400."""
        body = {"reason": reason} if reason else None
        await self._request("POST", f"/api/stock/batches/{batch_id}/write-off", token, body, ok=(200, 204))

    async def open_batch(self, token: str, batch_id: str, quantity: float | None = None) -> dict:
        """Вскрыть партию: вскрытая часть становится отдельной партией с коротким сроком. Без quantity — весь остаток.
        200 {id, expiresAt}; 400 — у продукта нет срока после вскрытия или количество больше остатка;
        409 — партия уже вскрыта, пуста или просрочена; 404."""
        body = {"quantity": plain(quantity)} if quantity is not None else {}
        return await self._request("POST", f"/api/stock/batches/{batch_id}/open", token, body)

    async def inventory(self, token: str, items: list[InventoryItemIn]) -> dict:
        """Инвентаризация пересчитанных продуктов. actual — сколько по факту, вместе с просроченным несписанным.
        200 {shortage, surplus, rows}: в rows только расхождения (accounted, actual, difference, value; недостача
        с минусом). 400 — например, излишек некуда записать: нет годной партии. Тогда не меняется ничего."""
        body = {"items": [{"productId": i["productId"], "actual": plain(i["actual"])} for i in items]}
        return await self._request("POST", "/api/stock/inventory", token, body)

    async def get_movements(self, token: str, take: int = 100) -> list[dict]:
        """Журнал операций, новые сверху. type: Supply, Order, WriteOff, PrepUse, PrepMade, Shortage, Surplus, Opened."""
        return await self._request("GET", "/api/movements", token, params={"take": take})

    async def get_stop_list(self, token: str) -> list[dict]:
        return await self._request("GET", "/api/stop-list", token)

    # Заготовки

    async def get_preps(self, token: str) -> list[dict]:
        """План заготовок: что есть, сколько приготовить (suggested) и сколько можно (maxQuantity)."""
        return await self._request("GET", "/api/preps", token)

    async def make_prep(self, token: str, product_id: str, quantity: float, prep_id: str | None = None) -> dict:
        """Приготовить заготовку со списанием сырья. Возвращает результат и при 201 (партия batchId, supplyNumber,
        expiresAt), и при 409 (shortages — чего не хватило, ничего не списано). 400 — не заготовка или неверное количество.
        prep_id — GUID попытки, как у заказа: повтор с ним сервер не задвоит, поэтому запрос повторяется и при таймауте."""
        body: dict[str, Any] = {"productId": product_id, "quantity": plain(quantity)}
        if prep_id:
            body["id"] = prep_id
        return await self._with_result_on_conflict(
            "POST", "/api/preps", token, body, "shortages", idempotent=prep_id is not None
        )

    # Аналитика и подсказки

    async def get_hot_offers(self, token: str) -> list[dict] | None:
        """Блюда, которые стоит продать, по убыванию приоритета; у части есть discount.
        None, если адреса нет в API (404 у старой версии)."""
        try:
            return await self._request("GET", "/api/hot-offers", token)
        except NotFound:
            return None

    async def get_hot_plan(self, token: str) -> dict | None:
        """Итог плана спасения: atRiskValue (сколько ₽ истекает), plannedValue (сколько уйдёт в блюда плана),
        portions, leftovers (что не пристроено: productName, unit, quantity, hoursLeft, value).
        Суммы пусты для ролей, которым не видны закупочные цены. None — адреса нет (старая версия API)."""
        try:
            return await self._request("GET", "/api/hot-offers/plan", token)
        except NotFound:
            return None

    async def get_purchase_suggestions(self, token: str) -> list[dict]:
        """Что и сколько заказать у поставщика (supplier, sum)."""
        return await self._request("GET", "/api/purchase-suggestions", token)

    async def get_write_off_analytics(self, token: str, days: int = 14) -> dict:
        """Списания за последние days дней: total, byDay, byProduct, byReason."""
        return await self._request("GET", "/api/analytics/write-offs", token, params={"days": days})

    async def get_stop_cost(self, token: str, days: int = 14) -> dict:
        """Недополученная выручка из-за стоп-листа: total и dishes."""
        return await self._request("GET", "/api/analytics/stop-cost", token, params={"days": days})

    # Меню для гостя

    async def get_public_menu(self, restaurant_id: str) -> dict:
        """Меню для гостя, без входа: {restaurant, dishes}. Блюд из стопа нет; offerPrice не null — блюдо
        в предложениях дня. 404 — ресторана нет."""
        return await self._request("GET", f"/api/public/menu/{restaurant_id}")

    # Сигналы

    async def get_alerts(self, token: str, take: int = 30) -> dict:
        """Сигналы ресторана, новые сверху: {unread, items}. У сигнала id (растёт), kind, title, detail.
        Виды: BatchExpiring, BatchExpired, DishStopped, DishBack, SupplyWaiting; сервер может добавить новые."""
        return await self._request("GET", "/api/alerts", token, params={"take": take})

    async def mark_alerts_read(self, token: str) -> None:
        """Отметить прочитанными все сигналы ресторана — и на сайте тоже."""
        await self._request("POST", "/api/alerts/read", token, ok=(200, 204))


def _product_body(name: str, unit: str, shelf_life_hours: int, cost: float, prep_recipe: list[RecipeItem] | None) -> dict:
    body: dict[str, Any] = {"name": name, "unit": unit, "shelfLifeHours": shelf_life_hours, "cost": plain(cost)}
    if prep_recipe:
        body["prepRecipe"] = [{"productId": r["productId"], "quantity": plain(r["quantity"])} for r in prep_recipe]
    return body


def _dish_body(
    name: str, price: float, recipe: list[RecipeItem], photo_url: str | None, category: str | None
) -> dict:
    body: dict[str, Any] = {
        "name": name,
        "price": plain(price),
        "recipe": [{"productId": r["productId"], "quantity": plain(r["quantity"])} for r in recipe],
    }
    if photo_url is not None:
        body["photoUrl"] = photo_url
    if category:
        body["category"] = category
    return body
