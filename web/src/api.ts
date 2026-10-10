import { useCallback, useEffect, useState } from 'react';

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
}

export interface SupplyRow {
  id: string;
  number: string;
  supplier: string | null;
  createdAt: string;
  total: number;
  items: { productName: string; unit: Unit; quantity: number }[];
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
  type: 'Supply' | 'Order' | 'WriteOff';
  productName: string;
  unit: Unit;
  quantity: number;
  note: string | null;
  actor: string;
}

export interface AlertRow {
  id: number;
  at: string;
  kind: 'BatchExpiring' | 'BatchExpired' | 'DishStopped' | 'DishBack';
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
  items: { dishId: string; dishName: string; quantity: number }[];
  shortages: Shortage[];
  writeOffs: { productName: string; unit: Unit; supplyNumber: string; quantity: number }[];
}

export interface Me {
  login: string;
  title: string;
  role: 'Manager' | 'Chef' | 'Cook' | 'Storekeeper';
  sections: string[];
  shift: { start: string; end: string };
  places: string[];
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

export const logout = () => setSession(null, null);

export interface LoginError {
  title: string;
  text: string;
}

const NO_SERVER: LoginError = { title: 'Сервер не отвечает', text: 'Войти сейчас нельзя. Попробуйте ещё раз через минуту.' };

/** Возвращает ошибку или null, если вход выполнен. */
export async function login(name: string, password: string): Promise<LoginError | null> {
  const response = await fetch('/api/auth/login', {
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

async function request(path: string, init?: RequestInit): Promise<Response> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: { ...init?.headers, Authorization: `Bearer ${getToken()}` },
  });
  if (response.status === 401) {
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

/**
 * Подключается к потоку событий сервера (SSE). Сообщение не несёт данных — оно только говорит,
 * что что-то изменилось; экраны перечитывают своё. Возвращает функцию отключения.
 */
export function connectEvents(): () => void {
  const token = getToken();
  if (!token || typeof EventSource === 'undefined') return () => {};
  const source = new EventSource(`/api/events?access_token=${encodeURIComponent(token)}`);
  let timer: ReturnType<typeof setTimeout> | undefined;
  source.onmessage = () => {
    // Несколько изменений подряд (заказ списывает сразу много партий) сливаются в одно обновление.
    clearTimeout(timer);
    timer = setTimeout(() => liveReloads.forEach((reload) => reload()), 150);
  };
  return () => {
    clearTimeout(timer);
    source.close();
  };
}

/**
 * Загружает данные и держит их свежими: сразу перечитывает по сигналу сервера,
 * а раз в refreshMs — на случай, если поток событий оборвался или изменилось только время.
 */
export function useApi<T>(path: string, refreshMs = 30_000) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(
    () =>
      get<T>(path)
        .then((d) => {
          setData(d);
          setError(null);
        })
        .catch((e: Error) => setError(e.message)),
    [path],
  );

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
