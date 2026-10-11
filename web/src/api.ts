import { useCallback, useEffect, useState, useRef } from 'react';
import { apiUrl } from './native';

export type Unit = 'Kg' | 'L' | 'Pcs';
export type DishStatus = 'Available' | 'Low' | 'Stop';
export type StopReason = 'Expired' | 'OutOfStock' | 'NotEnough' | 'Manual';
export type BatchStatus = 'Fresh' | 'ExpiringSoon' | 'Expired';

export interface HotIngredient {
  productId: string;
  productName: string;
  unit: Unit;
  atRiskQuantity: number;
  nearestQuantity: number;
  hoursLeft: number;
  alternativeDishes: number;
}

export interface HotOffer {
  dishId: string;
  dishName: string;
  photoUrl: string | null;
  price: number;
  portions: number;
  availablePortions: number;
  valueAtRisk: number;
  ingredients: HotIngredient[];
  reason: string;
  discount: { percent: number; price: number; floorPrice: number; reason: string } | null;
  score: number;
  /** Менеджер закрепил блюдо первым в списке. */
  pinned: boolean;
}

export interface HotRules {
  horizonHours: number;
  /** 0 — важна только сумма потерь, 50 — поровну, 100 — срок важнее вдвойне. */
  urgencyWeight: number;
  shareAcrossDishes: boolean;
  maxDiscountPercent: number;
}

export interface ExpiryCheck {
  batchId: string;
  supplyNumber: string;
  productId: string;
  productName: string;
  unit: Unit;
  quantity: number;
  remaining: number;
  createdAt: string;
  expiresAt: string;
  writtenOffAt: string | null;
}

export interface StopListRow {
  dishId: string;
  dishName: string;
  photoUrl: string | null;
  price: number;
  status: DishStatus;
  portions: number;
  limitingProductId: string;
  limitingProductName: string;
  reason: StopReason | null;
  stopForecastAt: string | null;
  cost: number;
  category: string | null;
}

export interface DishRow {
  id: string;
  name: string;
  price: number;
  photoUrl: string | null;
  recipe: { productId: string; quantity: number }[];
  category: string | null;
  manuallyStopped: boolean;
  hotPinned: boolean;
  hotHidden: boolean;
}

export interface PurchaseRow {
  productId: string;
  productName: string;
  unit: Unit;
  quantity: number;
  hoursCovered: number | null;
  reason: string;
  supplier: string | null;
  sum: number;
}

export interface WriteOffReport {
  total: number;
  byDay: { date: string; value: number }[];
  byProduct: { productId: string; productName: string; unit: Unit; quantity: number; value: number }[];
  byReason: { reason: string; value: number }[];
}

export interface DeficitRow {
  productId: string;
  productName: string;
  unit: Unit;
  rejectedOrders: number;
  missingQuantity: number;
  stopHours: number;
  dishes: string[];
}

export interface SupplierRow {
  supplier: string;
  supplies: number;
  batches: number;
  delivered: number;
  sold: number;
  expired: number;
  defective: number;
  spoiled: number;
  inStock: number;
  lossPercent: number;
  /** Средний срок партии при приёмке в процентах от норматива продукта. */
  shelfLifePercent: number;
  worstProduct: string | null;
  verdict: string;
}

export interface SupplierReport {
  days: number;
  delivered: number;
  lost: number;
  suppliers: SupplierRow[];
}

export interface StopCostReport {
  days: number;
  total: number;
  dishes: { dishId: string; dishName: string; stopHours: number; portionsPerHour: number; lostPortions: number; lostRevenue: number }[];
}

export interface AnalyticsSummary {
  orders: number;
  rejectedOrders: number;
  stopHours: number;
}

export interface BatchRow {
  id: string;
  productId: string;
  productName: string;
  unit: Unit;
  supplyNumber: string;
  quantity: number;
  remaining: number;
  value: number;
  receivedAt: string;
  expiresAt: string;
  hoursLeft: number;
  status: BatchStatus;
}

export interface StockRow {
  productId: string;
  productName: string;
  unit: Unit;
  fresh: number;
  expired: number;
  freshValue: number;
}

export interface ProductRow {
  id: string;
  name: string;
  unit: Unit;
  shelfLifeHours: number;
  cost: number;
  /** Состав на единицу продукта, если это заготовка; у покупных продуктов пусто. */
  prepRecipe: { productId: string; quantity: number }[];
}

export interface PrepRow {
  productId: string;
  productName: string;
  unit: Unit;
  shelfLifeHours: number;
  fresh: number;
  perDay: number;
  hoursCovered: number | null;
  suggested: number;
  reason: string | null;
  maxQuantity: number;
  ingredients: { productId: string; productName: string; unit: Unit; quantity: number; fresh: number }[];
}

export interface PrepResult {
  batchId: string;
  supplyNumber: string;
  quantity: number;
  expiresAt: string;
  shortages: { productId: string; productName: string; unit: Unit; required: number; available: number }[];
}

export interface SupplyRow {
  id: string;
  number: string;
  supplier: string | null;
  createdAt: string;
  total: number;
  items: { productName: string; unit: Unit; quantity: number }[];
}

/** Поставка от внешней системы: ждёт, пока её примут или отклонят. */
export interface IncomingRow {
  id: string;
  number: string;
  supplier: string | null;
  receivedAt: string;
  total: number;
  items: { productId: string; productName: string; unit: Unit; quantity: number; shelfLifeHours: number }[];
}

export interface ImportRow {
  line: number;
  productText: string;
  productId: string | null;
  quantity: number | null;
  unit: Unit | null;
  shelfLifeHours: number | null;
  price: number | null;
  status: 'Ok' | 'Warning' | 'Error';
  message: string;
}

export interface MovementRow {
  id: number;
  at: string;
  type: 'Supply' | 'Order' | 'WriteOff' | 'PrepUse' | 'PrepMade';
  productName: string;
  unit: Unit;
  quantity: number;
  note: string | null;
  actor: string;
}

export interface AlertRow {
  id: number;
  at: string;
  kind: 'BatchExpiring' | 'BatchExpired' | 'DishStopped' | 'DishBack' | 'SupplyWaiting';
  title: string;
  detail: string;
  expiresAt: string | null;
  read: boolean;
}

export interface AlertsResponse {
  unread: number;
  items: AlertRow[];
}

export interface ChatMessage {
  id: number;
  at: string;
  fromStaff: boolean;
  text: string;
}

export interface ChatRow {
  id: string;
  name: string;
  contact: string;
  lastMessageAt: string;
  unread: number;
  lastText: string;
  lastFromStaff: boolean;
}

export interface ChatDetails {
  id: string;
  name: string;
  contact: string;
  createdAt: string;
  messages: ChatMessage[];
}

export interface SimulatorSettings {
  running: boolean;
  ordersPerMinute: number;
}

export interface Shortage {
  productName: string;
  unit: Unit;
  required: number;
  available: number;
}

export interface OrderRow {
  id: string;
  number: number;
  createdAt: string;
  status: 'Accepted' | 'Rejected';
  place: string;
  total: number;
  items: { dishId: string; dishName: string; quantity: number; discountPercent: number; discountQuantity: number; price: number }[];
  /** Когда стол закрыли; null, пока заказ открыт. */
  closedAt: string | null;
  shortages: Shortage[];
  writeOffs: { productName: string; unit: Unit; supplyNumber: string; quantity: number }[];
}

/** Итоги с начала дня и столы с открытыми заказами — считает сервер по всем заказам. */
export interface OrderSummary {
  orders: number;
  rejected: number;
  revenue: number;
  tables: { place: string; total: number; last: string }[];
}

export interface Me {
  login: string;
  title: string;
  role: 'Manager' | 'Chef' | 'Cook' | 'Storekeeper' | 'Waiter';
  sections: string[];
  shift: { start: string; end: string };
  places: string[];
  /** Название ресторана, в котором работает сотрудник. */
  restaurant: string;
}

export interface RestaurantSettings {
  name: string;
  shiftStart: string;
  shiftEnd: string;
  tables: number;
  hasBar: boolean;
}

export interface LeadRow {
  id: number;
  createdAt: string;
  name: string;
  organization: string;
  contact: string;
  points: string;
  comment: string | null;
}

/** Ответ на создание ресторана: логин и пароль управляющего показываются один раз. */
export interface NewRestaurant {
  name: string;
  login: string;
  password: string;
}

export interface StaffRow {
  id: string;
  login: string;
  title: string;
  role: Me['role'];
}

const pluralRules = new Intl.PluralRules('ru-RU');
const TOKEN_KEY = 'ostatok.token';
const LOGIN_KEY = 'ostatok.login';
const listeners = new Set<() => void>();

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string | null) {
  try {
    if (value) localStorage.setItem(key, value);
    else localStorage.removeItem(key);
  } catch {
    // Хранилище недоступно (приватный режим): вход проживёт до перезагрузки страницы.
  }
}

export const getToken = () => read(TOKEN_KEY);
export const getLogin = () => read(LOGIN_KEY) ?? '';

function setSession(token: string | null, login: string | null) {
  write(TOKEN_KEY, token);
  write(LOGIN_KEY, login);
  listeners.forEach((l) => l());
}

export function onAuthChange(listener: () => void) {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}

export const logout = () => {
  // Следующий, кто войдёт в этом браузере, начинает со своего главного экрана, а не с чужой страницы.
  try {
    sessionStorage.removeItem('ostatok.next');
  } catch {
    // Без хранилища запоминать и нечего.
  }
  setSession(null, null);
};

export interface LoginError {
  title: string;
  text: string;
}

const NO_SERVER: LoginError = { title: 'Сервер не отвечает', text: 'Войти сейчас нельзя. Попробуйте ещё раз через минуту.' };

/** Возвращает ошибку или null, если вход выполнен. */
export async function login(name: string, password: string): Promise<LoginError | null> {
  const response = await fetch(apiUrl('/api/auth/login'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ login: name, password }),
  }).catch(() => null);
  if (!response) return NO_SERVER;
  if (response.status === 401 || response.status === 429) {
    const problem = await response.json().catch(() => ({}));
    if (typeof problem.attemptsLeft === 'number') {
      const left = pluralRules.select(problem.attemptsLeft) === 'one' ? 'Осталась' : 'Осталось';
      const word = { one: 'попытка', few: 'попытки' }[pluralRules.select(problem.attemptsLeft) as 'one' | 'few'] ?? 'попыток';
      return { title: 'Неверный логин или пароль', text: `Проверьте раскладку и Caps Lock. ${left} ${problem.attemptsLeft} ${word}.` };
    }
    if (typeof problem.retryAfterMinutes === 'number')
      return { title: 'Вход временно закрыт', text: `Слишком много неверных попыток. Вход откроется через ${problem.retryAfterMinutes} мин.` };
    return response.status === 401
      ? { title: 'Неверный логин или пароль', text: 'Проверьте раскладку и Caps Lock.' }
      : { title: 'Слишком много попыток', text: 'Подождите минуту и попробуйте снова.' };
  }
  if (!response.ok) return NO_SERVER;
  setSession((await response.json()).token, name);
  return null;
}

const EXPIRED_KEY = 'ostatok.expired';

/** true один раз после того, как сервер отозвал сессию: экран входа объясняет, почему человек на нём оказался. */
export function takeExpired(): boolean {
  try {
    const expired = sessionStorage.getItem(EXPIRED_KEY) !== null;
    sessionStorage.removeItem(EXPIRED_KEY);
    return expired;
  } catch {
    return false;
  }
}

async function request(path: string, init?: RequestInit): Promise<Response> {
  // Бесплатный сервер после простоя отвечает до минуты; дольше ждать нет смысла.
  const timeout = new AbortController();
  const timer = setTimeout(() => timeout.abort(), 75_000);
  let response: Response;
  try {
    response = await fetch(apiUrl(`/api${path}`), {
      ...init,
      signal: timeout.signal,
      headers: { ...init?.headers, Authorization: `Bearer ${getToken()}` },
    });
  } catch {
    throw new Error(navigator.onLine === false ? 'Нет подключения к интернету.' : 'Сервер не отвечает. Попробуйте ещё раз через минуту.');
  } finally {
    clearTimeout(timer);
  }
  if (response.status === 401) {
    try {
      sessionStorage.setItem(EXPIRED_KEY, '1');
    } catch {
      // Без хранилища экран входа просто не покажет пояснение.
    }
    logout();
    throw new Error('Сессия истекла, войдите заново.');
  }
  return response;
}

export async function get<T>(path: string): Promise<T> {
  const response = await request(path);
  if (!response.ok) throw new Error(`Сервер ответил ошибкой ${response.status}.`);
  return response.json();
}

/** Отправляет действие. Ответ с ошибкой не бросается: экран сам решает, что показать по коду. */
export function post(path: string, body?: unknown): Promise<Response> {
  return request(path, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/** Запрос с телом JSON и произвольным методом: PUT и DELETE для справочников. */
export function send(method: string, path: string, body?: unknown): Promise<Response> {
  return request(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/** Отправляет двоичные данные (фото блюда) с указанным типом. */
export function sendFile(method: string, path: string, body: Blob, contentType: string): Promise<Response> {
  return request(path, { method, headers: { 'Content-Type': contentType }, body });
}

/** Отправляет файл как есть: сервер сам определяет, Excel это или CSV. */
export function postFile(path: string, file: File): Promise<Response> {
  return request(path, { method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body: file });
}

// Все открытые на экране запросы: по сигналу сервера «данные изменились» они перечитываются.
const liveReloads = new Set<() => void>();

/** Перечитывает всё, что сейчас на экране: приложение зовёт это, когда его снова открыли. */
export const reloadAll = () => liveReloads.forEach((reload) => reload());

/**
 * Подключается к потоку событий сервера (SSE). Сообщение не несёт данных — оно только говорит,
 * что что-то изменилось; экраны перечитывают своё. Возвращает функцию отключения.
 */
export function connectEvents(): () => void {
  if (typeof EventSource === 'undefined') return () => {};
  let source: EventSource | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let retry: ReturnType<typeof setTimeout> | undefined;
  let stopped = false;

  const open = () => {
    const token = getToken();
    if (!token || stopped) return;
    source = new EventSource(apiUrl(`/api/events?access_token=${encodeURIComponent(token)}`));
    source.onmessage = () => {
      // Несколько изменений подряд (заказ списывает сразу много партий) сливаются в одно обновление.
      clearTimeout(timer);
      timer = setTimeout(reloadAll, 150);
    };
    // Если сервер перезапустился или засыпал, браузер закрывает поток насовсем — открываем его заново сами.
    source.onerror = () => {
      if (source?.readyState !== EventSource.CLOSED) return;
      clearTimeout(retry);
      retry = setTimeout(open, 5000);
    };
  };
  // Свёрнутая вкладка или приложение теряют поток незаметно: при возврате он открывается заново, а данные перечитываются.
  const onReturn = () => {
    if (document.hidden || stopped) return;
    source?.close();
    clearTimeout(retry);
    open();
    reloadAll();
  };

  document.addEventListener('visibilitychange', onReturn);
  open();
  return () => {
    stopped = true;
    document.removeEventListener('visibilitychange', onReturn);
    clearTimeout(timer);
    clearTimeout(retry);
    source?.close();
  };
}

/**
 * Загружает данные и держит их свежими: сразу перечитывает по сигналу сервера,
 * а раз в refreshMs — на случай, если поток событий оборвался или изменилось только время.
 */
export function useApi<T>(path: string, refreshMs = 30_000) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loaded = useRef(false);

  const reload = useCallback(
    () =>
      get<T>(path)
        .then((d) => {
          loaded.current = true;
          setData(d);
          setError(null);
        })
        // Короткий обрыв связи не должен стирать уже показанные данные: они остаются на экране до следующей удачной попытки.
        .catch((e: Error) => {
          if (!loaded.current) setError(e.message);
        }),
    [path],
  );

  // Другой адрес — другие данные: прежние «уже загружено» к ним не относятся.
  useEffect(() => {
    loaded.current = false;
  }, [path]);

  useEffect(() => {
    void reload();
    const timer = setInterval(reload, refreshMs);
    liveReloads.add(reload);
    return () => {
      clearInterval(timer);
      liveReloads.delete(reload);
    };
  }, [reload, refreshMs]);

  return { data, error, reload };
}
