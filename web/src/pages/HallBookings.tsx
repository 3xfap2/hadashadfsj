import { useState } from 'react';
import { useApi, type HallReservation, type HallView, type ReservationStatus } from '../api';
import { dm, guests, HallTabs, hhmm, ReservationForm, STATUS, StatusTag, useHallAction } from './HallCommon';

type FormState = { editing?: HallReservation } | null;

/** Брони на дату: поиск по гостю и телефону, фильтр по статусу, создание, перенос, отмена, неявка и посадка. */
export function HallBookings() {
  const hall = useApi<HallView>('/hall', 60_000);
  const [date, setDate] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState<ReservationStatus | ''>('');
  const [form, setForm] = useState<FormState>(null);
  const day = date ?? hall.data?.date ?? '';
  const list = useApi<HallReservation[]>(day ? `/hall/reservations?date=${day}` : '/hall/reservations');
  const action = useHallAction(() => Promise.all([list.reload(), hall.reload()]));

  const error = hall.error ?? list.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Брони не загрузились</h3><span>{error}</span></div>;
  const view = hall.data;
  if (!view || !list.data) return <p className="muted" role="status">Загружаем брони…</p>;

  const offset = view.utcOffsetMinutes;
  const all = list.data;
  const now = Date.parse(view.now);
  const text = query.trim().toLowerCase();
  // Поиск идёт по уже загруженным броням дня: имя и телефон на сервере зашифрованы.
  const rows = list.data
    .filter((r) => !status || r.status === status)
    .filter((r) => !text || r.guest.toLowerCase().includes(text) || (r.phone ?? '').toLowerCase().includes(text));
  const today = day === view.date;

  return (
    <>
      <div className="g-title">
        <h1>Брони</h1>
        <HallTabs manage={view.canManage} />
        <span className="end"><button className="g-btn g-btn--primary" type="button" onClick={() => setForm({})}>Новая бронь</button></span>
      </div>

      {form && (
        <ReservationForm view={{ ...view, date: day }} editing={form.editing}
          onDone={() => { setForm(null); void list.reload(); }} onCancel={() => setForm(null)} />
      )}

      <div className="zal-bookings__bar">
        <label className="sr" htmlFor="zb-date">Дата</label>
        <input className="g-input" id="zb-date" type="date" value={day} onChange={(e) => e.target.value && setDate(e.target.value)} />
        <label className="sr" htmlFor="zb-q">Поиск по гостю или телефону</label>
        <input className="g-input" id="zb-q" type="search" placeholder="Гость или телефон" value={query} onChange={(e) => setQuery(e.target.value)} />
        <div className="g-seg" role="group" aria-label="Статус">
          <button type="button" aria-pressed={status === ''} onClick={() => setStatus('')}>Все <span className="n">{list.data.length}</span></button>
          {(Object.keys(STATUS) as ReservationStatus[]).map((s) => (
            <button key={s} type="button" aria-pressed={status === s} onClick={() => setStatus(s)}>
              {STATUS[s]} <span className="n">{all.filter((r) => r.status === s).length}</span>
            </button>
          ))}
        </div>
      </div>

      {action.error && <p className="g-note g-note--err" role="alert">{action.error}</p>}

      <section className="g-panel" aria-labelledby="zb-h">
        <div className="g-panel__head">
          <h2 id="zb-h">{dm(day)}</h2>
          <span className="end">{guests(rows.filter((r) => r.status === 'Confirmed' || r.status === 'Seated').reduce((s, r) => s + r.guests, 0))} ожидаем и за столами · время ресторана</span>
        </div>
        {rows.length === 0 ? (
          <p className="g-empty"><strong>{list.data.length === 0 ? 'Броней на эту дату нет' : 'Ничего не найдено'}</strong>{list.data.length === 0 ? 'Нажмите «Новая бронь», чтобы записать гостя.' : 'Измените поиск или статус.'}</p>
        ) : (
          <ul className="g-rows zal-bookings">
            {rows.map((r) => {
              const noShowAt = Date.parse(r.noShowFrom);
              return (
                <li key={r.id}>
                  <span className="zal-list__time"><strong className="num hd">{hhmm(r.startsAt, offset)}</strong><span className="muted small num">до {hhmm(r.endsAt, offset)}</span></span>
                  <span>
                    <span className="g-name">{r.guest}</span>
                    <span className="muted small"> · {guests(r.guests)} · стол {r.tableNumber}, {r.zone}{r.phone ? ` · ${r.phone}` : ''}</span>
                    {r.note && <span className="g-why">{r.note}</span>}
                  </span>
                  <StatusTag status={r.status} />
                  {r.status === 'Confirmed' && (
                    <span className="zal-list__acts">
                      {today && <button className="g-btn g-btn--sm" type="button" disabled={action.busy} onClick={() => action.run('POST', `/hall/reservations/${r.id}/seat`)}>Посадить</button>}
                      <button className="g-btn g-btn--sm" type="button" onClick={() => setForm({ editing: r })}>Изменить</button>
                      {now >= noShowAt && <button className="g-btn g-btn--sm" type="button" disabled={action.busy} onClick={() => action.run('POST', `/hall/reservations/${r.id}/no-show`)}>Неявка</button>}
                      <button className="g-btn g-btn--sm" type="button" disabled={action.busy}
                        onClick={() => { if (confirm(`Отменить бронь: ${r.guest}, ${hhmm(r.startsAt, offset)}?`)) void action.run('POST', `/hall/reservations/${r.id}/cancel`); }}>Отменить</button>
                    </span>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </>
  );
}
