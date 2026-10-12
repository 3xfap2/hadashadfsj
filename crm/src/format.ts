const time = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const dayMonthDigits = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit' });
const fullDate = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
const pluralRules = new Intl.PluralRules('ru-RU');

export const date = (iso: string) => fullDate.format(new Date(iso));

export function plural(n: number, one: string, few: string, many: string): string {
  const rule = pluralRules.select(n);
  return `${n} ${rule === 'one' ? one : rule === 'few' ? few : many}`;
}

const DAY_WORDS: Record<number, string> = { [-1]: 'вчера', 0: 'сегодня', 1: 'завтра' };

/** «сегодня 23:52», «завтра 02:00», «14.10 08:00». */
export function dueLong(iso: string): string {
  const d = new Date(iso);
  const days = Math.round((startOfDay(d) - startOfDay(new Date())) / 86_400_000);
  const date = d.getFullYear() === new Date().getFullYear() ? dayMonthDigits.format(d) : `${dayMonthDigits.format(d)}.${d.getFullYear()}`;
  return `${DAY_WORDS[days] ?? date} ${time.format(d)}`;
}

/** Значение для поля «дата и время» в местном времени. */
export function toLocalInput(ms: number): string {
  const d = new Date(ms);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Конец сегодняшнего дня по часам пользователя: задачи до него — «на сегодня». */
export const dayEnd = () => startOfDay(new Date()) + 86_400_000;

function startOfDay(d: Date): number {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}
