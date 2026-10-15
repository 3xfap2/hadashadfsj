import { useEffect, useRef, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { get, problemText, send, type HallReservation, type HallView, type ReservationStatus, type TableOption, type TableState } from '../api';
import { plural } from '../format';
import './Hall.scss';

/** Время по часам ресторана: сдвиг пояса ресторана, а не браузера. */
const shifted = (iso: string, offset: number) => new Date(Date.parse(iso) + offset * 60_000).toISOString();
export const hhmm = (iso: string, offset: number) => shifted(iso, offset).slice(11, 16);
export const ymd = (iso: string, offset: number) => shifted(iso, offset).slice(0, 10);
/** «15.10» из «2026-10-15». */
export const dm = (date: string) => `${date.slice(8, 10)}.${date.slice(5, 7)}`;
export const guests = (n: number) => plural(n, 'гость', 'гостя', 'гостей');
export const seats = (n: number) => plural(n, 'место', 'места', 'мест');

export const STATE: Record<TableState, string> = {
  Free: 'Свободен',
  Reserved: 'Бронь',
  Occupied: 'Занят',
  Cleaning: 'Уборка',
  Blocked: 'Недоступен',
};

export const STATUS: Record<ReservationStatus, string> = {
  Confirmed: 'Подтверждена',
  Seated: 'За столом',
  Completed: 'Завершена',
  Cancelled: 'Отменена',
  NoShow: 'Неявка',
};

export function StateTag({ state }: { state: TableState }) {
  return <span className={`g-tag zal-tag zal-tag--${state.toLowerCase()}`}>{STATE[state]}</span>;
}

export function StatusTag({ status }: { status: ReservationStatus }) {
  return <span className={`g-tag zal-status zal-status--${status.toLowerCase()}`}>{STATUS[status]}</span>;
}

export function HallTabs({ manage }: { manage: boolean }) {
  return (
    <nav className="g-seg" aria-label="Раздел зала">
      <NavLink to="/app/hall" end>Схема</NavLink>
      <NavLink to="/app/hall/bookings">Брони</NavLink>
      <NavLink to="/app/hall/journal">Журнал</NavLink>
      {manage && <NavLink to="/app/hall/setup">Настройка</NavLink>}
    </nav>
  );
}

/**
 * Команда зала: отправляет действие, при отказе показывает причину от сервера (409 — стол занят, бронь изменена).
 * Успех показывается только после ответа сервера; данные экрана перечитываются.
 */
export function useHallAction(reload: () => unknown) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async (method: string, path: string, body?: unknown): Promise<Response | null> => {
    setBusy(true);
    setError(null);
    try {
      const response = await send(method, path, body);
      if (!response.ok) setError(await problemText(response));
      await reload();
      return response.ok ? response : null;
    } catch (e) {
      setError((e as Error).message);
      return null;
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, setError, run };
}

const DURATIONS = [60, 90, 120, 150, 180, 240];

/** Форма брони: создание или изменение (перенос). Подходящие столы и причины недоступности подбирает сервер. */
export function ReservationForm(props: {
  view: HallView;
  editing?: HallReservation | null;
  tableId?: string;
  onDone: () => void;
  onCancel: () => void;
}) {
  const { view, editing, onDone, onCancel } = props;
  const offset = view.utcOffsetMinutes;
  const [guest, setGuest] = useState(editing?.guest ?? '');
  const [phone, setPhone] = useState(editing?.phone ?? '');
  const [count, setCount] = useState(editing?.guests ?? 2);
  const [date, setDate] = useState(editing ? ymd(editing.startsAt, offset) : view.date);
  const [time, setTime] = useState(editing ? hhmm(editing.startsAt, offset) : roundUp(view.time));
  const [minutes, setMinutes] = useState(editing ? Math.round((Date.parse(editing.endsAt) - Date.parse(editing.startsAt)) / 60_000) : view.settings.visitMinutes);
  const [zone, setZone] = useState('');
  const [table, setTable] = useState(editing?.tableId ?? props.tableId ?? '');
  const [note, setNote] = useState(editing?.note ?? '');
  const [options, setOptions] = useState<TableOption[] | null>(null);
  const [optionsError, setOptionsError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Идентификатор новой брони: повторное нажатие после обрыва связи не создаст вторую бронь.
  const attempt = useRef<string>(crypto.randomUUID());
  const [stamp, setStamp] = useState(0);

  useEffect(() => {
    if (!date || !/^\d{2}:\d{2}$/.test(time) || count < 1) return;
    let stale = false;
    const query = `/hall/options?date=${date}&time=${time}&minutes=${minutes}&guests=${count}${editing ? `&reservation=${editing.id}` : ''}`;
    get<TableOption[]>(query)
      .then((o) => {
        if (stale) return;
        setOptions(o);
        setOptionsError(null);
        // Стол ещё не выбран — предлагается самый подходящий (сервер ставит его первым).
        setTable((t) => t || (o.find((x) => x.fits)?.tableId ?? ''));
      })
      .catch((e: Error) => { if (!stale) setOptionsError(e.message); });
    return () => { stale = true; };
  }, [date, time, minutes, count, editing, stamp]);

  const shown = (options ?? []).filter((o) => !zone || o.zoneId === zone);
  const chosen = options?.find((o) => o.tableId === table);
  const fits = shown.filter((o) => o.fits);
  const tooMany = count > Math.max(0, ...view.tables.map((t) => t.seats));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!chosen?.fits) {
      setError(chosen ? `Стол ${chosen.number}: ${chosen.reason}` : 'Выберите стол из подходящих.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const body = {
        id: editing ? editing.id : attempt.current, tableId: table, guest: guest.trim(), phone: phone.trim() || null, guests: count,
        date, time: `${time}:00`, minutes, note: note.trim() || null, version: editing?.version ?? null,
      };
      const response = editing ? await send('PUT', `/hall/reservations/${editing.id}`, body) : await send('POST', '/hall/reservations', body);
      if (response.ok) {
        onDone();
        return;
      }
      setError(await problemText(response));
      // Стол успели занять: доступность обновляется, чтобы выбрать другой.
      if (response.status === 409) setStamp((s) => s + 1);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="g-panel zal-form" aria-labelledby="zal-form-h">
      <div className="g-panel__head">
        <h2 id="zal-form-h">{editing ? `Бронь: ${editing.guest}` : 'Новая бронь'}</h2>
        <span className="end">время ресторана</span>
      </div>
      <form className="zal-form__body" onSubmit={submit}>
        <div className="zal-form__grid">
          <div className="g-field">
            <label htmlFor="zf-guest">Имя гостя</label>
            <input className="g-input" id="zf-guest" required maxLength={80} value={guest} onChange={(e) => setGuest(e.target.value)} autoComplete="off" />
          </div>
          <div className="g-field">
            <label htmlFor="zf-phone">Контакт <span className="muted small">необязательно</span></label>
            <input className="g-input" id="zf-phone" maxLength={30} inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} autoComplete="off" />
          </div>
          <div className="g-field">
            <label htmlFor="zf-count">Гостей</label>
            <input className="g-input" id="zf-count" type="number" min={1} max={50} required value={count} onChange={(e) => setCount(Math.max(1, Number(e.target.value) || 1))} />
            {tooMany && <span className="g-error-text">Такого большого стола в зале нет.</span>}
          </div>
          <div className="g-field">
            <label htmlFor="zf-date">Дата</label>
            <input className="g-input" id="zf-date" type="date" required value={date} onChange={(e) => setDate(e.target.value)} />
          </div>
          <div className="g-field">
            <label htmlFor="zf-time">Начало</label>
            <input className="g-input" id="zf-time" type="time" step={300} required value={time} onChange={(e) => setTime(e.target.value)} />
          </div>
          <div className="g-field">
            <label htmlFor="zf-min">Длительность</label>
            <select className="g-input" id="zf-min" value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
              {[...new Set([...DURATIONS, minutes])].sort((a, b) => a - b).map((m) => <option key={m} value={m}>{m % 60 === 0 ? `${m / 60} ч` : `${Math.floor(m / 60)} ч ${m % 60} мин`}</option>)}
            </select>
          </div>
        </div>

        <fieldset className="zal-pick">
          <legend className="lb">Стол <span className="muted small">— подходящие сверху; у занятых указана причина</span></legend>
          <div className="g-seg zal-pick__zones" role="group" aria-label="Зона">
            <button type="button" aria-pressed={zone === ''} onClick={() => setZone('')}>Все зоны</button>
            {view.zones.map((z) => <button key={z.id} type="button" aria-pressed={zone === z.id} onClick={() => setZone(z.id)}>{z.name}</button>)}
          </div>
          {optionsError && <p className="g-error-text">{optionsError}</p>}
          {!options && !optionsError && <p className="muted small" role="status">Подбираем столы…</p>}
          {options && fits.length === 0 && <p className="g-error-text">На это время {zone ? 'в этой зоне ' : ''}нет свободных столов на {guests(count)}. Попробуйте другое время{zone ? ' или зону' : ''}.</p>}
          <ul className="zal-pick__list">
            {shown.map((o) => (
              <li key={o.tableId}>
                <label className={`zal-pick__opt${o.fits ? '' : ' zal-pick__opt--off'}`}>
                  <input type="radio" name="zf-table" value={o.tableId} checked={table === o.tableId} disabled={!o.fits} onChange={() => setTable(o.tableId)} />
                  <strong className="num">Стол {o.number}</strong>
                  <span className="muted small">{o.zone} · {seats(o.seats)}</span>
                  {o.reason && <span className="zal-pick__why small">{o.reason}</span>}
                </label>
              </li>
            ))}
          </ul>
        </fieldset>

        <div className="g-field">
          <label htmlFor="zf-note">Комментарий</label>
          <input className="g-input" id="zf-note" maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} placeholder="День рождения, у окна, детский стул" />
        </div>

        {error && <p className="g-note g-note--err" role="alert">{error}</p>}
        <div className="zal-form__actions">
          <button className="g-btn g-btn--primary" type="submit" disabled={busy || !chosen?.fits || guest.trim() === ''}>
            {busy ? 'Сохраняем…' : editing ? 'Сохранить' : 'Забронировать'}
          </button>
          <button className="g-btn" type="button" onClick={onCancel}>Отмена</button>
        </div>
      </form>
    </section>
  );
}

/** Время формы по умолчанию — ближайшие пять минут вперёд. */
function roundUp(time: string): string {
  const [h, m] = time.split(':').map(Number);
  const total = Math.min(23 * 60 + 55, Math.ceil((h * 60 + m) / 5) * 5);
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}
