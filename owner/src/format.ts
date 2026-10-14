import type { Role, Unit } from './api';

const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });
const rub = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 0 });
const kopecks = new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 2 });
const time = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const dayMonthDigits = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit' });
const pluralRules = new Intl.PluralRules('ru-RU');

const UNITS: Record<Unit, string> = { Kg: 'кг', L: 'л', Pcs: 'шт' };

export const ROLES: { id: Role; title: string }[] = [
  { id: 'Waiter', title: 'Официант' },
  { id: 'Cook', title: 'Повар' },
  { id: 'Storekeeper', title: 'Кладовщик' },
  { id: 'Chef', title: 'Шеф-повар' },
  { id: 'Manager', title: 'Управляющий' },
];
export const roleTitle = (role: Role) => ROLES.find((r) => r.id === role)!.title;

export const money = (v: number) => rub.format(v);
/** Цена за единицу — с копейками: у дешёвых продуктов рубли без копеек не различают поставщиков. */
export const price = (v: number) => kopecks.format(v);
export const qty = (v: number, unit: Unit) => `${number.format(v)} ${UNITS[unit]}`;
export const unitName = (unit: Unit) => UNITS[unit];
export const percent = (v: number) => `${number.format(v)} %`;

export function plural(n: number, one: string, few: string, many: string): string {
  const rule = pluralRules.select(n);
  return `${n} ${rule === 'one' ? one : rule === 'few' ? few : many}`;
}

const DAY_WORDS: Record<number, string> = { [-1]: 'вчера', 0: 'сегодня', 1: 'завтра' };

/** «сегодня 23:52», «вчера 08:00», «14.10 08:00». */
export function dueLong(iso: string): string {
  const d = new Date(iso);
  const days = Math.round((startOfDay(d) - startOfDay(new Date())) / 86_400_000);
  const date = d.getFullYear() === new Date().getFullYear() ? dayMonthDigits.format(d) : `${dayMonthDigits.format(d)}.${d.getFullYear()}`;
  return `${DAY_WORDS[days] ?? date} ${time.format(d)}`;
}

function startOfDay(d: Date): number {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}
