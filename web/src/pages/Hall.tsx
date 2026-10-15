import { useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useApi, type HallReservation, type HallTableView, type HallView, type TableState } from '../api';
import { money, plural } from '../format';
import { dm, guests, HallTabs, hhmm, ymd, ReservationForm, seats, STATE, StateTag, StatusTag, useHallAction } from './HallCommon';

type FormState = { editing?: HallReservation; tableId?: string } | null;

/** Раздел «Зал»: схема столов с состоянием на сейчас или на выбранный момент, карточка стола и ближайшие брони. */
export function Hall() {
  const [plan, setPlan] = useState<{ date: string; time: string } | null>(null);
  const [zone, setZone] = useState('');
  const [state, setState] = useState<TableState | ''>('');
  const [minSeats, setMinSeats] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(null);
  const hall = useApi<HallView>(plan ? `/hall?date=${plan.date}&time=${plan.time}` : '/hall', 30_000);
  const action = useHallAction(hall.reload);

  if (hall.error) return <div className="g-note g-note--err" role="alert"><h3>Зал не загрузился</h3><span>{hall.error}</span></div>;
  const view = hall.data;
  if (!view) return <p className="muted" role="status">Загружаем зал…</p>;

  const offset = view.utcOffsetMinutes;
  const zoneName = view.zones.find((z) => z.id === zone)?.name ?? 'Все зоны';
  const inZone = view.tables.filter((t) => !zone || t.zoneId === zone);
  const matches = (t: HallTableView) => (!state || t.state === state) && t.seats >= minSeats;
  const current = view.tables.find((t) => t.id === selected) ?? null;
  const zonesShown = view.zones.filter((z) => !zone || z.id === zone);
  const dayReservations = view.reservations.filter((r) => (!zone || inZone.some((t) => t.id === r.tableId)));
  const expected = dayReservations.filter((r) => r.status === 'Confirmed');
  const upcoming = expected.filter((r) => Date.parse(r.endsAt) > Date.parse(view.at)).slice(0, 8);
  const seatedGuests = inZone.reduce((s, t) => s + (t.state === 'Occupied' ? t.visit?.guests ?? 0 : 0), 0);

  if (view.tables.length === 0) {
    return (
      <>
        <div className="g-title"><h1>Зал</h1><HallTabs manage={view.canManage} /></div>
        <p className="g-empty">
          <strong>Схема зала ещё не настроена</strong>
          {view.canManage ? 'Добавьте зоны и столы во вкладке «Настройка» — после этого здесь появятся брони и посадка.' : 'Схему настраивает управляющий. Пока её нет, заказы оформляются по номеру стола, как раньше.'}
        </p>
      </>
    );
  }

  return (
    <>
      <div className="g-title">
        <h1>Зал</h1>
        <HallTabs manage={view.canManage} />
        <span className="end">
          <button className="g-btn g-btn--primary" type="button" onClick={() => setForm({})}>Новая бронь</button>
        </span>
      </div>

      {form && (
        <ReservationForm view={view} editing={form.editing} tableId={form.tableId}
          onDone={() => { setForm(null); void hall.reload(); }} onCancel={() => setForm(null)} />
      )}

      <div className="zal-filters">
        <div className="g-seg" role="group" aria-label="Режим">
          <button type="button" aria-pressed={!plan} onClick={() => setPlan(null)}>Сейчас</button>
          <button type="button" aria-pressed={Boolean(plan)} onClick={() => setPlan({ date: view.date, time: view.time })}>План</button>
        </div>
        {plan && (
          <>
            <label className="sr" htmlFor="zal-date">Дата</label>
            <input className="g-input zal-filters__date" id="zal-date" type="date" value={plan.date} onChange={(e) => e.target.value && setPlan({ ...plan, date: e.target.value })} />
            <label className="sr" htmlFor="zal-time">Время</label>
            <input className="g-input zal-filters__time" id="zal-time" type="time" step={900} value={plan.time} onChange={(e) => e.target.value && setPlan({ ...plan, time: e.target.value })} />
          </>
        )}
        <label className="sr" htmlFor="zal-state">Статус</label>
        <select className="g-input zal-filters__sel" id="zal-state" value={state} onChange={(e) => setState(e.target.value as TableState | '')}>
          <option value="">Все статусы</option>
          {(Object.keys(STATE) as TableState[]).map((s) => <option key={s} value={s}>{STATE[s]}</option>)}
        </select>
        <label className="sr" htmlFor="zal-seats">Мест не меньше</label>
        <select className="g-input zal-filters__sel" id="zal-seats" value={minSeats} onChange={(e) => setMinSeats(Number(e.target.value))}>
          <option value={0}>Любое число мест</option>
          {[2, 4, 6, 8].map((n) => <option key={n} value={n}>от {seats(n)}</option>)}
        </select>
      </div>

      <div className="g-seg zal-zones" role="group" aria-label="Зона">
        <button type="button" aria-pressed={zone === ''} onClick={() => setZone('')}>Все зоны <span className="n">{view.tables.length}</span></button>
        {view.zones.map((z) => (
          <button key={z.id} type="button" aria-pressed={zone === z.id} onClick={() => setZone(z.id)}>
            {z.name} <span className="n">{view.tables.filter((t) => t.zoneId === z.id).length}</span>
          </button>
        ))}
      </div>

      <dl className="g-kpis zal-kpis" aria-label={`Сводка: ${zoneName}, ${dm(view.date)} ${view.time}`}>
        <div className="g-kpi g-kpi--main"><dt>Свободно</dt><dd className="v num">{inZone.filter((t) => t.state === 'Free').length}<span className="n"> из {inZone.length}</span></dd></div>
        <div className="g-kpi"><dt>Занято</dt><dd className="v num">{inZone.filter((t) => t.state === 'Occupied').length}</dd><dd className="n">{guests(seatedGuests)} за столами</dd></div>
        <div className="g-kpi"><dt>Ожидаем по брони</dt><dd className="v num">{expected.length}</dd><dd className="n">{guests(expected.reduce((s, r) => s + r.guests, 0))} на {dm(view.date)}</dd></div>
        <div className="g-kpi"><dt>Уборка и закрыты</dt><dd className="v num">{inZone.filter((t) => t.state === 'Cleaning' || t.state === 'Blocked').length}</dd><dd className="n">{zoneName} · {view.live ? `сейчас, ${view.time}` : `план на ${view.time}`}</dd></div>
      </dl>

      {action.error && <p className="g-note g-note--err" role="alert">{action.error}</p>}

      <div className="zal-main">
        <div className="zal-floors">
          {zonesShown.map((z) => {
            const tables = view.tables.filter((t) => t.zoneId === z.id);
            return (
              <section key={z.id} className="g-panel" aria-labelledby={`zal-z-${z.id}`}>
                <div className="g-panel__head">
                  <h2 id={`zal-z-${z.id}`}>{z.name}</h2>
                  <span className="end">{tablesWord(tables.length)} · {seats(tables.reduce((s, t) => s + t.seats, 0))}</span>
                </div>
                {tables.length === 0 ? <p className="g-empty">В зоне пока нет столов.</p> : (
                  <Floor tables={tables} selected={selected} onSelect={setSelected} dim={(t) => !matches(t)} offset={offset} />
                )}
              </section>
            );
          })}
          <Legend />
        </div>

        <aside className="zal-side">
          {current ? (
            <TableCard key={current.id} table={current} view={view} busy={action.busy}
              run={action.run} onBook={() => setForm({ tableId: current.id })} onEdit={(r) => setForm({ editing: r })} />
          ) : (
            <section className="g-panel zal-card"><p className="g-empty"><strong>Выберите стол</strong>На схеме — номер, места и ближайшая бронь. Карточка стола покажет гостей, брони и действия.</p></section>
          )}
        </aside>
      </div>

      <section className="g-panel" aria-labelledby="zal-next-h">
        <div className="g-panel__head">
          <h2 id="zal-next-h">Ближайшие брони</h2>
          <span className="end">{dm(view.date)} · {zoneName}</span>
        </div>
        {upcoming.length === 0 ? <p className="g-empty"><strong>Броней больше нет</strong>На {dm(view.date)} после {view.time} никого не ждём.</p> : (
          <ul className="g-rows zal-list">
            {upcoming.map((r) => (
              <li key={r.id}>
                <span className="zal-list__time"><strong className="num hd">{hhmm(r.startsAt, offset)}</strong><span className="muted small num">до {hhmm(r.endsAt, offset)}</span></span>
                <span><span className="g-name">{r.guest}</span><span className="muted small"> · {guests(r.guests)}{r.note ? ` · ${r.note}` : ''}</span></span>
                <button className="g-btn g-btn--sm zal-list__table" type="button" onClick={() => setSelected(r.tableId)}>Стол {r.tableNumber}</button>
                <StatusTag status={r.status} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}

const tablesWord = (n: number) => plural(n, 'стол', 'стола', 'столов');

function Legend() {
  return (
    <p className="zal-legend small muted" aria-hidden="true">
      {(Object.keys(STATE) as TableState[]).map((s) => <span key={s}><i className={`zal-dot zal-dot--${s.toLowerCase()}`} />{STATE[s]}</span>)}
    </p>
  );
}

/**
 * Схема зоны: столы в процентах от размеров зала. На телефоне те же столы идут сеткой карточек.
 * onMove — перетаскивание в настройке схемы: координаты в процентах.
 */
export function Floor(props: {
  tables: HallTableView[];
  selected: string | null;
  onSelect: (id: string) => void;
  dim?: (t: HallTableView) => boolean;
  offset: number;
  onMove?: (id: string, x: number, y: number) => void;
}) {
  const { tables, selected, onSelect, dim, offset, onMove } = props;
  const floor = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: string; dx: number; dy: number; moved: boolean } | null>(null);

  const down = (t: HallTableView) => (e: ReactPointerEvent<HTMLButtonElement>) => {
    if (!onMove || !floor.current) return;
    const box = floor.current.getBoundingClientRect();
    drag.current = { id: t.id, dx: ((e.clientX - box.left) / box.width) * 100 - t.x, dy: ((e.clientY - box.top) / box.height) * 100 - t.y, moved: false };
    e.currentTarget.setPointerCapture(e.pointerId);
  };
  const move = (t: HallTableView) => (e: ReactPointerEvent<HTMLButtonElement>) => {
    const d = drag.current;
    if (!onMove || !d || d.id !== t.id || !floor.current) return;
    const box = floor.current.getBoundingClientRect();
    const x = Math.round(Math.min(100 - t.w, Math.max(0, ((e.clientX - box.left) / box.width) * 100 - d.dx)));
    const y = Math.round(Math.min(100 - t.h, Math.max(0, ((e.clientY - box.top) / box.height) * 100 - d.dy)));
    if (x !== t.x || y !== t.y) {
      d.moved = true;
      onMove(t.id, x, y);
    }
  };
  const up = () => { drag.current = null; };

  return (
    <div className={`zal-floor${onMove ? ' zal-floor--edit' : ''}`} ref={floor}>
      {tables.map((t) => {
        const hint = t.state === 'Occupied' && t.visit ? `${guests(t.visit.guests)} · ${t.label.replace('Занят ', '')}`
          : t.state === 'Free' && t.next ? `бронь ${hhmm(t.next.startsAt, offset)}`
          : t.label;
        return (
          <button
            key={t.id}
            type="button"
            className={`zal-t zal-t--${t.shape.toLowerCase()} zal-t--${t.state.toLowerCase()}${dim?.(t) ? ' zal-t--dim' : ''}${t.warning ? ' zal-t--warn' : ''}`}
            style={{ left: `${t.x}%`, top: `${t.y}%`, width: `${t.w}%`, height: `${t.h}%` }}
            aria-pressed={selected === t.id}
            aria-label={`Стол ${t.number}, ${seats(t.seats)}: ${t.label}${t.warning ? `. ${t.warning}` : ''}`}
            onClick={() => onSelect(t.id)}
            onPointerDown={down(t)}
            onPointerMove={move(t)}
            onPointerUp={up}
            onPointerCancel={up}
          >
            <strong className="num">{t.number}</strong>
            <span className="zal-t__seats">{seats(t.seats)}</span>
            <span className="zal-t__state">{STATE[t.state]}</span>
            <span className="zal-t__hint">{hint}</span>
          </button>
        );
      })}
    </div>
  );
}

/** Карточка выбранного стола: кто сидит, следующие брони, открытые заказы и действия для текущего состояния. */
function TableCard(props: {
  table: HallTableView;
  view: HallView;
  busy: boolean;
  run: (method: string, path: string, body?: unknown) => Promise<Response | null>;
  onBook: () => void;
  onEdit: (r: HallReservation) => void;
}) {
  const { table: t, view, busy, run, onBook, onEdit } = props;
  const offset = view.utcOffsetMinutes;
  const zone = view.zones.find((z) => z.id === t.zoneId)?.name ?? '';
  const [count, setCount] = useState(Math.min(2, t.seats));
  const [minutes, setMinutes] = useState(view.settings.visitMinutes);
  const [blockReason, setBlockReason] = useState('');
  // Посадка без брони: один идентификатор на попытку — двойное нажатие не посадит гостей дважды.
  const attempt = useRef(crypto.randomUUID());
  const now = Date.parse(view.now);
  const reservation = t.state === 'Reserved' ? t.next : null;

  const seatWalkIn = async () => {
    const ok = await run('POST', `/hall/tables/${t.id}/seat`, { id: attempt.current, guests: count, minutes });
    if (ok) attempt.current = crypto.randomUUID();
  };

  return (
    <section className="g-panel zal-card" aria-labelledby="zal-card-h">
      <div className="g-panel__head">
        <h2 id="zal-card-h">Стол {t.number}</h2>
        <span className="end"><StateTag state={t.state} /></span>
      </div>
      <div className="zal-card__body">
        <p className="muted">{zone} · {seats(t.seats)} · {t.shape === 'Round' ? 'круглый' : 'прямоугольный'}</p>
        <p className="hd">{t.label}</p>
        {t.warning && <p className="g-note zal-warn" role="status">{t.warning}</p>}
        {!view.live && <p className="muted small">План на {dm(view.date)} {view.time}: посадка и уборка — в режиме «Сейчас».</p>}

        {t.visit && (
          <div className="zal-card__box">
            <h3>За столом</h3>
            <p>{t.visit.guest ? `${t.visit.guest} · ` : ''}{guests(t.visit.guests)}</p>
            <p className="muted small num">с {hhmm(t.visit.seatedAt, offset)} · ожидаем уход в {hhmm(t.visit.expectedEndAt, offset)} · посадил {t.visit.seatedBy}</p>
          </div>
        )}
        {t.block && (
          <div className="zal-card__box">
            <h3>Стол закрыт</h3>
            <p>{t.block.reason}</p>
            <p className="muted small">{t.block.to ? `до ${dm(ymd(t.block.to, offset))} ${hhmm(t.block.to, offset)}` : 'до снятия'} · {t.block.createdBy}</p>
          </div>
        )}
        {t.orders.length > 0 && (
          <div className="zal-card__box">
            <h3>Открытые заказы</h3>
            <ul className="zal-card__orders">
              {t.orders.map((o) => (
                <li key={o.number}><span className="num">№ {o.number} · {hhmm(o.createdAt, offset)}</span><span className="num">{money(o.total)}</span>{o.ready && <span className="muted small">готов</span>}</li>
              ))}
            </ul>
            <p className="muted small">Счёт закрывается в разделе «Заказы»: завершение визита оплату не проводит.</p>
          </div>
        )}

        {view.live && (
          <div className="zal-card__actions">
            {reservation && (
              <>
                <button className="g-btn g-btn--primary g-btn--block" type="button" disabled={busy} onClick={() => run('POST', `/hall/reservations/${reservation.id}/seat`)}>
                  Гости пришли — посадить ({reservation.guest})
                </button>
                <button className="g-btn g-btn--block" type="button" disabled={busy || now < Date.parse(reservation.noShowFrom)}
                  title={now < Date.parse(reservation.noShowFrom) ? `Неявку можно отметить с ${hhmm(reservation.noShowFrom, offset)}` : undefined}
                  onClick={() => run('POST', `/hall/reservations/${reservation.id}/no-show`)}>
                  Неявка{now < Date.parse(reservation.noShowFrom) ? ` — с ${hhmm(reservation.noShowFrom, offset)}` : ''}
                </button>
              </>
            )}
            {t.state === 'Occupied' && t.visit && (
              <>
                <button className="g-btn g-btn--primary g-btn--block" type="button" disabled={busy} onClick={() => run('POST', `/hall/visits/${t.visit!.id}/finish`)}>Гости ушли — на уборку</button>
                <button className="g-btn g-btn--block" type="button" disabled={busy} onClick={() => run('POST', `/hall/visits/${t.visit!.id}/extend`, { minutes: 30 })}>Задерживаются — продлить на 30 мин</button>
              </>
            )}
            {t.state === 'Cleaning' && (
              <button className="g-btn g-btn--primary g-btn--block" type="button" disabled={busy} onClick={() => run('POST', `/hall/tables/${t.id}/ready`)}>Стол убран — готов</button>
            )}
            {(t.state === 'Free' || t.state === 'Reserved') && (
              <div className="zal-seat">
                <h3>Посадить без брони</h3>
                <div className="zal-seat__row">
                  <span className="g-qty" role="group" aria-label="Гостей">
                    <button type="button" aria-label="Меньше гостей" disabled={count <= 1} onClick={() => setCount(count - 1)}>−</button>
                    <span aria-live="polite">{count}</span>
                    <button type="button" aria-label="Больше гостей" disabled={count >= t.seats} onClick={() => setCount(count + 1)}>+</button>
                  </span>
                  <label className="sr" htmlFor="zal-seat-min">На сколько</label>
                  <select className="g-input" id="zal-seat-min" value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
                    {[...new Set([30, 45, 60, 90, 120, 180, view.settings.visitMinutes])].sort((a, b) => a - b).map((m) => <option key={m} value={m}>{m} мин</option>)}
                  </select>
                  <button className="g-btn" type="button" disabled={busy} onClick={seatWalkIn}>Посадить</button>
                </div>
                {t.freeUntil && <p className="muted small">Свободен до {hhmm(t.freeUntil, offset)} с учётом перерыва {view.settings.bufferMinutes} мин.</p>}
              </div>
            )}
          </div>
        )}

        <div className="zal-card__box">
          <h3>Брони стола</h3>
          {t.upcoming.length === 0 ? <p className="muted small">Будущих броней нет.</p> : (
            <ul className="zal-card__res">
              {t.upcoming.map((r) => (
                <li key={r.id}>
                  <span className="num hd">{ymd(r.startsAt, offset) === view.date ? "" : `${dm(ymd(r.startsAt, offset))} `}{hhmm(r.startsAt, offset)}–{hhmm(r.endsAt, offset)}</span>
                  <span>{r.guest} · {guests(r.guests)}{r.phone ? ` · ${r.phone}` : ''}{r.note ? ` · ${r.note}` : ''}</span>
                  <span className="zal-card__res-act">
                    <button className="g-btn g-btn--sm" type="button" onClick={() => onEdit(r)}>Изменить</button>
                    <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => { if (confirm(`Отменить бронь: ${r.guest}, ${hhmm(r.startsAt, offset)}?`)) void run('POST', `/hall/reservations/${r.id}/cancel`); }}>Отменить</button>
                  </span>
                </li>
              ))}
            </ul>
          )}
          {t.state !== 'Blocked' && <button className="g-btn g-btn--block" type="button" onClick={onBook}>Забронировать этот стол</button>}
        </div>

        {view.canManage && (
          <div className="zal-card__box">
            <h3>Доступность</h3>
            {t.block ? (
              <button className="g-btn g-btn--block" type="button" disabled={busy} onClick={() => run('POST', `/hall/blocks/${t.block!.id}/lift`)}>Открыть стол</button>
            ) : (
              <form className="zal-seat__row" onSubmit={async (e) => {
                e.preventDefault();
                const body = { reason: blockReason.trim() };
                const first = await run('POST', `/hall/tables/${t.id}/block`, body);
                if (first) setBlockReason('');
                else if (t.upcoming.length > 0 && confirm('На это время есть брони. Отменить их и закрыть стол? Лучше сначала перенести брони на другие столы.')) {
                  if (await run('POST', `/hall/tables/${t.id}/block`, { ...body, cancelReservations: true })) setBlockReason('');
                }
              }}>
                <label className="sr" htmlFor="zal-block">Причина</label>
                <input className="g-input" id="zal-block" required maxLength={200} placeholder="Причина: ремонт, банкет" value={blockReason} onChange={(e) => setBlockReason(e.target.value)} />
                <button className="g-btn" type="submit" disabled={busy || t.state === 'Occupied'}>Закрыть стол</button>
              </form>
            )}
          </div>
        )}
      </div>
    </section>
  );
}
