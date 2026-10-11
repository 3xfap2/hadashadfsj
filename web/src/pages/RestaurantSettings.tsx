import { useEffect, useState, type FormEvent } from 'react';
import { send, useApi, type RestaurantSettings as Settings } from '../api';

/** Настройки ресторана: название, часы смены и зал. Меняет управляющий. */
export function RestaurantSettings({ onSaved }: { onSaved: () => void }) {
  const settings = useApi<Settings>('/restaurant', 300_000);
  const [form, setForm] = useState<Settings | null>(null);
  const [note, setNote] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!form && settings.data) setForm(settings.data);
  }, [form, settings.data]);

  if (!form) return null;

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    const response = await send('PUT', '/restaurant', form);
    if (response.ok) {
      setNote({ kind: 'ok', text: 'Сохранено. Зал и часы смены обновятся у сотрудников при следующем открытии кабинета.' });
      onSaved();
    } else {
      const problem = await response.json().catch(() => ({}));
      setNote({ kind: 'err', text: Object.values((problem.errors ?? {}) as Record<string, string[]>).flat().join(' ') || `Сервер ответил ошибкой ${response.status}.` });
    }
    setBusy(false);
  };

  return (
    <form className="g-panel" onSubmit={save} noValidate>
      <div className="g-panel__head"><h2>Ресторан</h2><span className="end">название, смена, зал</span></div>
      <div className="form-body">
        <div className="g-field">
          <label htmlFor="r-title">Название</label>
          <input className="g-input" id="r-title" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </div>
        <div className="rest-two">
          <div className="g-field">
            <label htmlFor="r-start">Смена с</label>
            <input className="g-input num" id="r-start" placeholder="12:00" value={form.shiftStart} onChange={(e) => setForm({ ...form, shiftStart: e.target.value })} />
          </div>
          <div className="g-field">
            <label htmlFor="r-end">до</label>
            <input className="g-input num" id="r-end" placeholder="24:00" value={form.shiftEnd} onChange={(e) => setForm({ ...form, shiftEnd: e.target.value })} />
          </div>
        </div>
        <span className="g-hint">Если смена заканчивается после полуночи, укажите время как есть: 18:00 – 02:00.</span>
        <div className="g-field">
          <label htmlFor="r-tables">Столов в зале</label>
          <input className="g-input num" id="r-tables" inputMode="numeric" value={form.tables} onChange={(e) => setForm({ ...form, tables: Number(e.target.value) || 0 })} />
          <span className="g-hint">От 0 до 60. По ним официант выбирает, куда записать заказ.</span>
        </div>
        <label className="hot-check">
          <input type="checkbox" checked={form.hasBar} onChange={(e) => setForm({ ...form, hasBar: e.target.checked })} />
          <span>Есть бар — заказы можно записывать на него</span>
        </label>
        {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
      </div>
      <div className="g-panel__foot cat-foot">
        <span />
        <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Сохранить</button>
      </div>
    </form>
  );
}
