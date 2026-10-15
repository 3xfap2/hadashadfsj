import { useState } from 'react';
import { useApi, type HallEventRow, type HallView } from '../api';
import { dm, HallTabs, hhmm, ymd } from './HallCommon';

const KINDS: { id: string; label: string }[] = [
  { id: '', label: 'Все' },
  { id: 'reservation', label: 'Брони' },
  { id: 'visit', label: 'Визиты' },
  { id: 'table', label: 'Столы' },
  { id: 'block', label: 'Блокировки' },
  { id: 'zone', label: 'Зоны и настройки' },
];

/** Журнал зала: кто, когда и что сделал с бронями, визитами и схемой. */
export function HallJournal() {
  const hall = useApi<HallView>('/hall', 60_000);
  const events = useApi<HallEventRow[]>('/hall/journal?take=300');
  const [kind, setKind] = useState('');

  const error = hall.error ?? events.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Журнал не загрузился</h3><span>{error}</span></div>;
  if (!hall.data || !events.data) return <p className="muted" role="status">Загружаем журнал…</p>;
  const offset = hall.data.utcOffsetMinutes;
  const rows = events.data.filter((e) => !kind || e.entity === kind || (kind === 'zone' && e.entity === 'settings'));

  return (
    <>
      <div className="g-title">
        <h1>Журнал зала</h1>
        <HallTabs manage={hall.data.canManage} />
      </div>
      <div className="g-seg" role="group" aria-label="Что показывать">
        {KINDS.map((k) => <button key={k.id} type="button" aria-pressed={kind === k.id} onClick={() => setKind(k.id)}>{k.label}</button>)}
      </div>
      <section className="g-panel">
        {rows.length === 0 ? <p className="g-empty"><strong>Записей нет</strong>Здесь появятся брони, посадки, уборка и изменения схемы.</p> : (
          <ul className="g-rows zal-journal">
            {rows.map((e) => (
              <li key={e.id}>
                <span className="num muted small">{dm(ymd(e.at, offset))} {hhmm(e.at, offset)}</span>
                <span><span className="hd">{e.text}</span> <span className="muted small">· {e.actor || 'система'}</span></span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
