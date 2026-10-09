import type { Unit } from './api';

const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });
const rub = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 0 });
const time = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const dayMonth = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short' });
const weekdayShort = new Intl.DateTimeFormat('ru-RU', { weekday: 'short' });
const longDate = new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });
const pluralRules = new Intl.PluralRules('ru-RU');

const UNITS: Record<Unit, string> = { Kg: 'кг', L: 'л', Pcs: 'шт' };

export const money = (v: number) => rub.format(v);
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

export const isToday = (iso: string) => startOfDay(new Date(iso)) === startOfDay(new Date());

function startOfDay(d: Date): number {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}
