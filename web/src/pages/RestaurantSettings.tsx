import { useEffect, useState, type FormEvent } from 'react';
import { post, problemText, send, useApi, type IntegrationKey, type RestaurantSettings as Settings } from '../api';

/** Настройки ресторана: название, часы смены, зал, автосписание просрочки и ключ для поставок извне. Меняет управляющий. */
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
    // Обрыв связи бросает исключение: без finally кнопка осталась бы серой до перезагрузки страницы.
    try {
      const response = await send('PUT', '/restaurant', form);
      if (response.ok) {
        setNote({ kind: 'ok', text: 'Сохранено. Зал и часы смены обновятся у сотрудников при следующем открытии кабинета.' });
        onSaved();
      } else {
        setNote({ kind: 'err', text: await problemText(response) });
      }
    } catch (e) {
      setNote({ kind: 'err', text: (e as Error).message });
    } finally {
      setBusy(false);
    }
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
        <label className="hot-check">
          <input type="checkbox" checked={form.autoWriteOff} onChange={(e) => setForm({ ...form, autoWriteOff: e.target.checked })} />
          <span>Списывать просрочку автоматически</span>
        </label>
        {form.autoWriteOff && (
          <div className="g-field">
            <label htmlFor="r-grace">Через сколько часов после истечения срока</label>
            <div className="g-unit rest-hours">
              <input className="g-input num" id="r-grace" inputMode="numeric" value={form.autoWriteOffAfterHours} onChange={(e) => setForm({ ...form, autoWriteOffAfterHours: Number(e.target.value) || 0 })} />
              <span>ч</span>
            </div>
          </div>
        )}
        <span className="g-hint">
          {form.autoWriteOff
            ? 'Просрочка ждёт ручного списания это время (но не дольше, чем продукт хранился), потом система спишет её сама. От 0 до 168 часов.'
            : 'По умолчанию выключено: просроченная партия остаётся на складе «под списание», в заказы не идёт, а списывает её сотрудник кнопкой — так он видит, что выбрасывает.'}
        </span>
        {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
      </div>
      <div className="g-panel__foot cat-foot">
        <span />
        <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Сохранить</button>
      </div>
      <SupplyKey issued={form.hasIntegrationKey} onIssued={() => setForm({ ...form, hasIntegrationKey: true })} />
    </form>
  );
}

/**
 * Ключ интеграции: с ним учётная система или сервис поставщика присылает поставки в кабинет — без входа сотрудника.
 * Ключ показывается один раз; новый ключ сразу отключает прежний.
 */
function SupplyKey({ issued, onIssued }: { issued: boolean; onIssued: () => void }) {
  const [key, setKey] = useState('');
  const [state, setState] = useState({ busy: false, error: '', copied: false });

  const issue = async () => {
    if (issued && !window.confirm('Выдать новый ключ? Прежний сразу перестанет работать — его придётся заменить у поставщика.')) return;
    setState({ busy: true, error: '', copied: false });
    try {
      const response = await post('/restaurant/integration-key');
      if (response.ok) {
        setKey(((await response.json()) as IntegrationKey).key);
        onIssued();
        setState({ busy: false, error: '', copied: false });
      } else {
        setState({ busy: false, error: await problemText(response), copied: false });
      }
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, copied: false });
    }
  };
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(key);
      setState((s) => ({ ...s, copied: true }));
    } catch {
      // Буфер обмена недоступен (нет разрешения): ключ виден на экране, его можно выделить и скопировать вручную.
    }
  };

  return (
    <div className="form-body rest-key">
      <h3>Поставки извне</h3>
      <span className="muted">
        Учётная система или сервис поставщика присылает поставку методом <code>POST /api/supplies/incoming</code> с ключом в заголовке <code>X-Api-Key</code>.
        Поставка появится в «Склад → Поставки» и встанет на склад только после приёмки. Ключ даёт право только присылать поставки в этот ресторан.
      </span>
      {key && (
        <div className="g-note g-note--ok" role="status">
          <h3>Ключ выдан — скопируйте его сейчас</h3>
          <span>Он показывается один раз: у нас хранится только его отпечаток.</span>
          <code className="rest-key__value">{key}</code>
          <span className="rest-key__acts">
            <button className="g-btn g-btn--sm" type="button" onClick={copy}>{state.copied ? 'Скопировано' : 'Скопировать'}</button>
            <a className="g-btn g-btn--sm" href="/supplier/" target="_blank" rel="noreferrer">Открыть страницу поставщика</a>
          </span>
        </div>
      )}
      {state.error && <span className="g-error-text" role="alert">{state.error}</span>}
      <span className="rest-key__acts">
        <button className="g-btn" type="button" disabled={state.busy} onClick={issue}>{issued ? 'Выдать новый ключ' : 'Выдать ключ'}</button>
        {!key && <span className="muted small">{issued ? 'Ключ выдан. Если он потерян, выдайте новый.' : 'Ключ ещё не выдавался.'}</span>}
      </span>
    </div>
  );
}
