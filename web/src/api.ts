import { useCallback, useEffect, useState } from 'react';

export type Unit = 'Kg' | 'L' | 'Pcs';
export type DishStatus = 'Available' | 'Low' | 'Stop';
export type StopReason = 'Expired' | 'OutOfStock' | 'NotEnough';
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
}

export interface StopListRow {
  dishId: string;
  dishName: string;
  photoUrl: string | null;
  price: number;
  status: DishStatus;
  portions: number;
  limitingProductName: string;
  reason: StopReason | null;
  stopForecastAt: string | null;
}

export interface BatchRow {
  id: string;
  productName: string;
  unit: Unit;
  supplyNumber: string;
  remaining: number;
  value: number;
  expiresAt: string;
  hoursLeft: number;
  status: BatchStatus;
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

/** Возвращает текст ошибки или null, если вход выполнен. */
export async function login(name: string, password: string): Promise<string | null> {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ login: name, password }),
  });
  if (response.status === 401 || response.status === 429) {
    const problem = await response.json().catch(() => ({}));
    if (typeof problem.attemptsLeft === 'number') {
      const left = pluralRules.select(problem.attemptsLeft) === 'one' ? 'Осталась' : 'Осталось';
      const word = { one: 'попытка', few: 'попытки' }[pluralRules.select(problem.attemptsLeft) as 'one' | 'few'] ?? 'попыток';
      return `Проверьте раскладку и Caps Lock. ${left} ${problem.attemptsLeft} ${word}.`;
    }
    if (typeof problem.retryAfterMinutes === 'number')
      return `Слишком много неверных попыток. Вход откроется через ${problem.retryAfterMinutes} мин.`;
    return response.status === 401 ? 'Проверьте раскладку и Caps Lock.' : 'Слишком много попыток. Подождите минуту и попробуйте снова.';
  }
  if (!response.ok) return 'Сервер не отвечает. Попробуйте ещё раз.';
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

/** Загружает данные и обновляет их раз в refreshMs, пока экран открыт. */
export function useApi<T>(path: string, refreshMs = 15_000) {
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
    return () => clearInterval(timer);
  }, [reload, refreshMs]);

  return { data, error, reload };
}
