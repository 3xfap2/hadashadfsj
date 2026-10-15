import { useState } from 'react';
import { useApi, type HallSettings, type HallTableView, type HallView, type TableShape } from '../api';
import { Floor } from './Hall';
import { HallTabs, useHallAction } from './HallCommon';

type Draft = { zoneId: string; number: number; seats: number; shape: TableShape; x: number; y: number; w: number; h: number; version: number | null };

const NEW = 'new';

/** Настройка зала для управляющего: зоны, столы (номер, места, форма, положение, размер) и правила броней. */
export function HallSetup() {
  const hall = useApi<HallView>('/hall', 60_000);
  const action = useHallAction(hall.reload);
  const [zone, setZone] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [selected, setSelected] = useState<string | null>(null);
  const [zoneName, setZoneName] = useState('');
  const [rename, setRename] = useState<string | null>(null);
  const [settings, setSettings] = useState<HallSettings | null>(null);

  if (hall.error) return <div className="g-note g-note--err" role="alert"><h3>Зал не загрузился</h3><span>{hall.error}</span></div>;
  const view = hall.data;
  if (!view) return <p className="muted" role="status">Загружаем зал…</p>;
  if (!view.canManage) return <p className="g-empty"><strong>Схему настраивает управляющий</strong>У вашей роли нет доступа к настройке зала.</p>;

  const zoneId = zone ?? view.zones[0]?.id ?? '';
  const rules = settings ?? view.settings;
  const draftOf = (t: HallTableView): Draft => drafts[t.id] ?? { zoneId: t.zoneId, number: t.number, seats: t.seats, shape: t.shape, x: t.x, y: t.y, w: t.w, h: t.h, version: t.version };
  const tables = view.tables.filter((t) => draftOf(t).zoneId === zoneId);
  const preview: HallTableView[] = [
    ...tables.map((t) => ({ ...t, ...draftOf(t) })),
    ...(drafts[NEW] && drafts[NEW].zoneId === zoneId ? [{ ...view.tables[0], ...emptyView, id: NEW, ...drafts[NEW], version: 0 }] : []),
  ] as HallTableView[];
  const edit = (id: string, base: Draft, patch: Partial<Draft>) => setDrafts((d) => ({ ...d, [id]: { ...(d[id] ?? base), ...patch } }));
  const nextNumber = Math.max(0, ...view.tables.map((t) => t.number)) + 1;

  const saveTable = async (id: string) => {
    const d = drafts[id];
    if (!d) return;
    const ok = id === NEW ? await action.run('POST', '/hall/tables', d) : await action.run('PUT', `/hall/tables/${id}`, d);
    if (ok) setDrafts(({ [id]: _, ...rest }) => rest);
  };
  const archive = async (t: HallTableView) => {
    if (!confirm(`Убрать стол ${t.number} в архив? История броней и визитов останется.`)) return;
    await action.run('PUT', `/hall/tables/${t.id}`, { ...draftOf(t), archived: true, version: t.version });
  };

  return (
    <>
      <div className="g-title">
        <h1>Настройка зала</h1>
        <HallTabs manage />
      </div>

      {action.error && <p className="g-note g-note--err" role="alert">{action.error}</p>}

      <section className="g-panel" aria-labelledby="zs-rules">
        <div className="g-panel__head"><h2 id="zs-rules">Правила броней</h2><span className="end">в минутах</span></div>
        <form className="zal-settings" onSubmit={async (e) => { e.preventDefault(); if (await action.run('PUT', '/hall/settings', rules)) setSettings(null); }}>
          {([
            ['visitMinutes', 'Визит по умолчанию', 15, 720],
            ['bufferMinutes', 'Перерыв между гостями', 0, 120],
            ['noShowMinutes', 'Ждём опоздавшего', 0, 120],
            ['warnMinutes', 'Бронь видна за', 0, 240],
          ] as const).map(([key, label, min, max]) => (
            <div className="g-field" key={key}>
              <label htmlFor={`zs-${key}`}>{label}</label>
              <input className="g-input" id={`zs-${key}`} type="number" min={min} max={max} required value={rules[key]}
                onChange={(e) => setSettings({ ...rules, [key]: Number(e.target.value) })} />
            </div>
          ))}
          <button className="g-btn g-btn--primary" type="submit" disabled={action.busy || !settings}>Сохранить</button>
        </form>
      </section>

      <div className="g-seg" role="group" aria-label="Зона">
        {view.zones.map((z) => <button key={z.id} type="button" aria-pressed={zoneId === z.id} onClick={() => { setZone(z.id); setSelected(null); }}>{z.name}</button>)}
      </div>

      <section className="g-panel" aria-labelledby="zs-zones">
        <div className="g-panel__head"><h2 id="zs-zones">Зоны</h2></div>
        <div className="zal-settings">
          <form className="zal-seat__row" style={{ gridColumn: '1 / -1' }} onSubmit={async (e) => {
            e.preventDefault();
            if (await action.run('POST', '/hall/zones', { name: zoneName.trim(), sort: view.zones.length })) setZoneName('');
          }}>
            <label className="sr" htmlFor="zs-zone">Новая зона</label>
            <input className="g-input" id="zs-zone" required maxLength={60} placeholder="Новая зона: Терраса, VIP" value={zoneName} onChange={(e) => setZoneName(e.target.value)} />
            <button className="g-btn" type="submit" disabled={action.busy}>Добавить зону</button>
          </form>
          {zoneId && (() => {
            const z = view.zones.find((x) => x.id === zoneId)!;
            return (
              <form className="zal-seat__row" style={{ gridColumn: '1 / -1' }} onSubmit={async (e) => {
                e.preventDefault();
                if (await action.run('PUT', `/hall/zones/${z.id}`, { name: (rename ?? z.name).trim(), sort: z.sort })) setRename(null);
              }}>
                <label className="sr" htmlFor="zs-rename">Название зоны</label>
                <input className="g-input" id="zs-rename" required maxLength={60} value={rename ?? z.name} onChange={(e) => setRename(e.target.value)} />
                <button className="g-btn" type="submit" disabled={action.busy || rename === null}>Переименовать</button>
                <button className="g-btn" type="button" disabled={action.busy || tables.length > 0} title={tables.length > 0 ? 'Сначала уберите столы зоны в архив' : undefined}
                  onClick={async () => { if (confirm(`Убрать зону «${z.name}» в архив?`) && await action.run('PUT', `/hall/zones/${z.id}`, { name: z.name, sort: z.sort, archived: true })) setZone(null); }}>
                  В архив
                </button>
              </form>
            );
          })()}
        </div>
      </section>

      {zoneId && (
        <div className="zal-setup">
          <section className="g-panel" aria-labelledby="zs-preview">
            <div className="g-panel__head"><h2 id="zs-preview">Схема</h2><span className="end">перетащите стол, затем «Сохранить»</span></div>
            <Floor tables={preview} selected={selected} onSelect={setSelected} offset={view.utcOffsetMinutes}
              onMove={(id, x, y) => {
                const t = view.tables.find((v) => v.id === id);
                const base = id === NEW ? drafts[NEW] : t && draftOf(t);
                if (base) edit(id, base, { x, y });
                setSelected(id);
              }} />
          </section>

          <section className="g-panel" aria-labelledby="zs-tables">
            <div className="g-panel__head">
              <h2 id="zs-tables">Столы</h2>
              <button className="g-btn g-btn--sm end" type="button" disabled={Boolean(drafts[NEW])}
                onClick={() => { setDrafts((d) => ({ ...d, [NEW]: { zoneId, number: nextNumber, seats: 4, shape: 'Rect', x: 5, y: 5, w: 16, h: 18, version: null } })); setSelected(NEW); }}>
                Добавить стол
              </button>
            </div>
            <div className="zal-setup__rows">
              {drafts[NEW] && drafts[NEW].zoneId === zoneId && (
                <TableRow id={NEW} draft={drafts[NEW]} view={view} current={selected === NEW} busy={action.busy}
                  onChange={(patch) => edit(NEW, drafts[NEW], patch)} onSave={() => saveTable(NEW)}
                  onDrop={() => setDrafts(({ [NEW]: _, ...rest }) => rest)} />
              )}
              {tables.length === 0 && !drafts[NEW] && <p className="g-empty">В зоне нет столов. Нажмите «Добавить стол».</p>}
              {tables.map((t) => (
                <TableRow key={t.id} id={t.id} draft={draftOf(t)} view={view} current={selected === t.id} busy={action.busy} dirty={Boolean(drafts[t.id])}
                  onChange={(patch) => edit(t.id, draftOf(t), patch)} onSave={() => saveTable(t.id)}
                  onDrop={() => setDrafts(({ [t.id]: _, ...rest }) => rest)} onArchive={() => archive(t)} />
              ))}
            </div>
          </section>
        </div>
      )}
    </>
  );
}

const emptyView = { state: 'Free', label: 'Новый стол', warning: null, visit: null, next: null, block: null, cleaningSince: null, freeUntil: null, upcoming: [], orders: [] };

function TableRow(props: {
  id: string; draft: Draft; view: HallView; current: boolean; busy: boolean; dirty?: boolean;
  onChange: (patch: Partial<Draft>) => void; onSave: () => void; onDrop: () => void; onArchive?: () => void;
}) {
  const { id, draft: d, view, current, busy, dirty, onChange, onSave, onDrop, onArchive } = props;
  const num = (key: keyof Draft, label: string, min: number, max: number) => (
    <label>
      {label}
      <input className="g-input num" type="number" min={min} max={max} value={d[key] as number} onChange={(e) => onChange({ [key]: Number(e.target.value) })} />
    </label>
  );
  return (
    <div className="zal-setup__row" aria-current={current} aria-label={id === NEW ? 'Новый стол' : `Стол ${d.number}`}>
      {num('number', 'Номер', 1, 60)}
      {num('seats', 'Мест', 1, 30)}
      <label>
        Форма
        <select className="g-input" value={d.shape} onChange={(e) => onChange({ shape: e.target.value as TableShape })}>
          <option value="Rect">Прямоугольный</option>
          <option value="Round">Круглый</option>
        </select>
      </label>
      <label>
        Зона
        <select className="g-input" value={d.zoneId} onChange={(e) => onChange({ zoneId: e.target.value })}>
          {view.zones.map((z) => <option key={z.id} value={z.id}>{z.name}</option>)}
        </select>
      </label>
      {num('x', 'Слева, %', 0, 100)}
      {num('y', 'Сверху, %', 0, 100)}
      {num('w', 'Ширина, %', 3, 50)}
      {num('h', 'Высота, %', 3, 50)}
      <div className="zal-setup__acts">
        <button className="g-btn g-btn--sm g-btn--primary" type="button" disabled={busy || (id !== NEW && !dirty)} onClick={onSave}>{id === NEW ? 'Добавить' : 'Сохранить'}</button>
        {(id === NEW || dirty) && <button className="g-btn g-btn--sm" type="button" onClick={onDrop}>Отменить</button>}
        {onArchive && !dirty && <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={onArchive}>В архив</button>}
      </div>
    </div>
  );
}
