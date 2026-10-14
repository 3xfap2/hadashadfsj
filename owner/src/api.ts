import { useCallback, useEffect, useState, useRef } from 'react';

export type Role = 'Manager' | 'Chef' | 'Cook' | 'Storekeeper' | 'Waiter';
export type Unit = 'Kg' | 'L' | 'Pcs';

export interface Me {
  login: string;
  title: string;
  role: Role;
  /** Разделы учётной записи; кабинет владельца открыт тем, у кого есть «owner». */
  sections: string[];
  restaurant: string;
  restaurantId: string;
}

export interface WriteOffs {
  expired: number;
  spoiled: number;
  defective: number;
  total: number;
}

export interface RestaurantSummary {
  id: string;
  name: string;
  revenue: number;
  orders: number;
  writeOffs: WriteOffs;
  stopCost: number;
  /** Блюд в стопе сейчас. */
  stopped: number;
  /** Просроченных партий на складе, ждут списания. */
  toWriteOff: number;
  /** Партий, у которых срок истекает в ближайшие сутки. */
  expiringSoon: number;
}

export interface OwnerSummary {
  days: number;
  from: string;
  restaurants: RestaurantSummary[];
  total: RestaurantSummary;
  topWrittenOff: { productName: string; unit: Unit; times: number; quantity: number; value: number; restaurants: number }[];
}

export interface StaffRow {
  restaurantId: string;
  restaurant: string;
  id: string;
  login: string;
  title: string;
  role: Role;
  /** Это сам владелец. */
  owner: boolean;
}

export interface SupplierRow {
  place: number;
  supplier: string;
  restaurants: number;
  supplies: number;
  delivered: number;
  defectivePercent: number;
  spoiledPercent: number;
  expiredPercent: number;
  lossPercent: number;
  shelfLifePercent: number;
  score: number;
  verdict: string;
  products: { name: string; unit: Unit; quantity: number; averagePrice: number }[];
}

export interface Suppliers {
  days: number;
  formula: string;
  delivered: number;
  suppliers: SupplierRow[];
}

export interface Dialog {
  restaurantId: string;
  restaurant: string;
  /** Логин сотрудника; null — сообщения всем сотрудникам ресторана. */
  employee: string | null;
  lastAt: string;
  lastText: string;
  lastFromOwner: boolean;
  unread: number;
}

export interface Message {
  id: number;
  at: string;
  fromOwner: boolean;
  author: string;
  toAll: boolean;
  text: string;
}

/** Ресторан владельца из «Моих ресторанов» сайта: тот же список и тот же переход. */
export interface MyRestaurant {
  id: string;
  name: string;
  current: boolean;
}

const pluralRules = new Intl.PluralRules('ru-RU');
// Ключи те же, что у кабинета ресторана: владелец, вошедший на сайте, второй раз не входит, а «Открыть ресторан» сразу попадает в кабинет.
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

// Последние ответы GET за сессию: экран открывается с ними сразу, а свежие данные подтягиваются в фоне.
const cache = new Map<string, unknown>();
// Одинаковые GET, запрошенные одновременно, уходят на сервер один раз.
const inflight = new Map<string, Promise<unknown>>();
// Ключ включает сессию: следующий вошедший не увидит данные предыдущего.
const cacheKey = (path: string) => `${getToken()} ${path}`;

function setSession(token: string | null, login: string | null) {
  cache.clear();
  inflight.clear();
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
      ? { title: 'Неверный логин или пароль', text: problem.detail || 'Проверьте раскладку и Caps Lock.' }
      : { title: 'Слишком много попыток', text: 'Подождите минуту и попробуйте снова.' };
  }
  if (!response.ok) {
    // Отключённому ресторану сервер объясняет отказ своим текстом.
    const problem = await response.json().catch(() => ({}));
    return problem.detail ? { title: 'Вход закрыт', text: problem.detail } : NO_SERVER;
  }
  setSession((await response.json()).token, name);
  return null;
}

/** «Открыть ресторан»: сервер выдаёт токен с этим рестораном, и кабинет ресторана открывается уже в нём. Возвращает текст ошибки. */
export async function enterRestaurant(id: string): Promise<string | null> {
  const response = await send('POST', `/my/restaurants/${id}/enter`);
  if (!response.ok) return problem(response);
  setSession((await response.json()).token, read(LOGIN_KEY));
  location.assign('/app');
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
    response = await fetch(`/api${path}`, {
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
  // После записи уже летящие GET могут нести прежнее состояние: следующий запрос пойдёт на сервер заново.
  if (init?.method && init.method !== 'GET') inflight.clear();
  return response;
}

/** Текст ошибки из ответа сервера: поля ValidationProblem, иначе detail, иначе код. */
export async function problem(response: Response): Promise<string> {
  const body = await response.json().catch(() => ({}));
  return Object.values((body.errors ?? {}) as Record<string, string[]>).flat().join(' ') || body.detail || `Сервер ответил ошибкой ${response.status}.`;
}

export function get<T>(path: string): Promise<T> {
  const key = cacheKey(path);
  const running = inflight.get(key);
  if (running) return running as Promise<T>;
  const started: Promise<T> = request(path)
    .then(async (response) => {
      if (!response.ok) throw new Error(await problem(response));
      return response.json() as Promise<T>;
    })
    .then((data) => {
      if (inflight.get(key) === started) cache.set(key, data);
      return data;
    })
    .finally(() => {
      if (inflight.get(key) === started) inflight.delete(key);
    });
  inflight.set(key, started);
  return started;
}

/** Отправляет действие. Ответ с ошибкой не бросается: экран сам решает, что показать по коду. */
export function send(method: string, path: string, body?: unknown): Promise<Response> {
  return request(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

// Все открытые на экране запросы: после действия они перечитываются.
const liveReloads = new Set<() => void>();

/** Перечитывает всё, что сейчас на экране: после своего действия. */
export const reloadAll = () => {
  // Запрос, начатый до действия, мог прочитать старые данные.
  inflight.clear();
  liveReloads.forEach((reload) => reload());
};

/**
 * Загружает данные и держит их свежими: перечитывает после своих действий и раз в refreshMs.
 * Поток событий сервера здесь не подключается: он приходит по одному ресторану, а кабинет владельца смотрит на все сразу.
 */
export function useApi<T>(path: string, refreshMs = 30_000) {
  const [data, setData] = useState<T | null>(() => (cache.get(cacheKey(path)) as T | undefined) ?? null);
  const [error, setError] = useState<string | null>(null);
  const loaded = useRef(data !== null);
  // Номер последнего запроса: ответ, который пришёл позже более нового (или уже для другого адреса), отбрасывается.
  const latest = useRef(0);

  const reload = useCallback(() => {
    const mine = ++latest.current;
    return (
      get<T>(path)
        .then((d) => {
          if (mine !== latest.current) return;
          loaded.current = true;
          setData(d);
          setError(null);
        })
        // Короткий обрыв связи не должен стирать уже показанные данные: они остаются на экране до следующей удачной попытки.
        .catch((e: Error) => {
          if (mine === latest.current && !loaded.current) setError(e.message);
        })
    );
  }, [path]);

  // Другой адрес — другие данные: прежние «уже загружено» к ним не относятся. Если этот адрес уже открывали — показываем, что знаем.
  useEffect(() => {
    const known = cache.get(cacheKey(path)) as T | undefined;
    loaded.current = known !== undefined;
    setData(known ?? null);
    setError(null);
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

/**
 * Действие по кнопке: пока идёт запрос, кнопки заблокированы; ошибка сервера показывается текстом.
 * После удачи всё открытое на экране перечитывается.
 */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const run = async (action: () => Promise<Response>, done?: (response: Response) => unknown) => {
    setBusy(true);
    setError('');
    try {
      const response = await action();
      if (!response.ok) return setError(await problem(response));
      await done?.(response);
      reloadAll();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return { busy, error, run };
}
