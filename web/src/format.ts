import type { Unit } from './api';

const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });
const rub = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 0 });
const time = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const dayMonth = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short' });
const weekdayShort = new Intl.DateTimeFormat('ru-RU', { weekday: 'short' });
const longDate = new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });
const pluralRules = new Intl.PluralRules('ru-RU');

const UNITS: Record<Unit, string> = { Kg: 'кг', L: 'л', Pcs: 'шт' };

const dayMonthDigits = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit' });
const weekdayFull = new Intl.DateTimeFormat('ru-RU', { weekday: 'long' });

export const money = (v: number) => rub.format(v);
export const unitName = (unit: Unit) => UNITS[unit];
export const dayMonthShort = (iso: string) => dayMonthDigits.format(new Date(iso));
export const weekdayLong = (iso: string) => weekdayFull.format(new Date(iso));

/** Количество в техкарте: меньше килограмма или литра — в граммах и миллилитрах, как пишут повара. */
export function recipeQty(v: number, unit: Unit): string {
  if (unit === 'Kg' && v < 1) return `${number.format(v * 1000)} г`;
  if (unit === 'L' && v < 1) return `${number.format(v * 1000)} мл`;
  return qty(v, unit);
}

/** Срок хранения: до пяти суток — в часах, как на этикетках; дольше — в днях. */
export const shelfLife = (hours: number) => (hours <= 120 ? `${hours} ч` : plural(Math.round(hours / 24), 'день', 'дня', 'дней'));
export const qty = (v: number, unit: Unit) => `${number.format(v)} ${UNITS[unit]}`;
export const weekday = (iso: string) => weekdayShort.format(new Date(iso));
export const today = () => longDate.format(new Date());
export const clock = (iso: string) => time.format(new Date(iso));

export function plural(n: number, one: string, few: string, many: string): string {
  const rule = pluralRules.select(n);
  return `${n} ${rule === 'one' ? one : rule === 'few' ? few : many}`;
}

export const portions = (n: number) => plural(n, 'порция', 'порции', 'порций');

export function duration(hours: number): string {
  const h = Math.abs(hours);
  if (h < 1) return `${Math.max(1, Math.round(h * 60))} мин`;
  if (h < 48) return `${Math.round(h)} ч`;
  return `${Math.round(h / 24)} дн`;
}

const NEAR_DAYS: Record<number, string> = { [-1]: 'вчера ', 0: '', 1: 'завтра ', 2: 'послезавтра ' };

/** «до 23:52», «до завтра 10:00», «до послезавтра 14:00», «до 12 окт. 14:00» — относительно сегодняшнего дня. */
export function until(iso: string): string {
  const date = new Date(iso);
  const days = Math.round((startOfDay(date) - startOfDay(new Date())) / 86_400_000);
  return `до ${NEAR_DAYS[days] ?? `${dayMonth.format(date)} `}${time.format(date)}`;
}

const DAY_WORDS: Record<number, string> = { [-1]: 'вчера', 0: 'сегодня', 1: 'завтра' };

/** «сегодня 23:52», «завтра 02:00», «14.10 08:00». */
export function dueLong(iso: string): string {
  const date = new Date(iso);
  const days = Math.round((startOfDay(date) - startOfDay(new Date())) / 86_400_000);
  return `${DAY_WORDS[days] ?? dayMonthDigits.format(date)} ${time.format(date)}`;
}

/** Остаток срока подробно: «5 ч 12 мин», «1 день 15 ч», «4 дня»; для просрочки — «−3 ч». */
export function leftPrecise(hours: number): string {
  if (hours < 0) return `−${duration(hours)}`;
  const minutes = Math.round(hours * 60);
  if (minutes < 60) return `${Math.max(1, minutes)} мин`;
  if (hours < 24) return minutes % 60 === 0 ? `${minutes / 60} ч` : `${Math.floor(minutes / 60)} ч ${minutes % 60} мин`;
  const days = Math.floor(hours / 24);
  const rest = Math.floor(hours - days * 24);
  return days < 2 && rest > 0 ? `${plural(days, 'день', 'дня', 'дней')} ${rest} ч` : plural(days, 'день', 'дня', 'дней');
}

/** Значение для поля «дата и время» в местном времени. */
export function toLocalInput(ms: number): string {
  const d = new Date(ms);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export const isToday = (iso: string) => startOfDay(new Date(iso)) === startOfDay(new Date());

function startOfDay(d: Date): number {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}
