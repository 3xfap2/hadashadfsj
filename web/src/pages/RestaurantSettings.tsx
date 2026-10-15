import { useEffect, useState, type FormEvent } from 'react';
import { post, problemText, send, useApi, type IntegrationKey, type RestaurantSettings as Settings } from '../api';

/** Целое число из поля ввода; пусто, дробь или не число — null (а не 0: пустое поле не должно молча стать нулём). */
const whole = (text: string) => {
  const value = Number(text.trim().replace(',', '.'));
  return text.trim() !== '' && Number.isInteger(value) ? value : null;
};

/** Настройки ресторана: название, часы смены, зал, автосписание просрочки и ключ для поставок извне. Меняет управляющий. */
export function RestaurantSettings({ onSaved }: { onSaved: () => void }) {
  const settings = useApi<Settings>('/restaurant', 300_000);
  const [form, setForm] = useState<Settings | null>(null);
  // Числа хранятся строкой, как их набрали: так «1,5» не превращается на ходу в «15», а стёртое поле — в «0».
  const [text, setText] = useState({ tables: '', grace: '' });
  const [tried, setTried] = useState(false);
  const [note, setNote] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!form && settings.data) {
      setForm(settings.data);
      setText({ tables: String(settings.data.tables), grace: String(settings.data.autoWriteOffAfterHours) });
    }
  }, [form, settings.data]);

  if (!form) return null;

  const tables = whole(text.tables);
  const grace = whole(text.grace);
  const tablesError = tables === null || tables < 0 || tables > 60;
  // Отсрочка проверяется, только когда автосписание включено; выключено — сохраняется прежняя.
  const graceError = form.autoWriteOff && (grace === null || grace < 1 || grace > 168);

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    if (tablesError || graceError) return;
    setBusy(true);
    // Обрыв связи бросает исключение: без finally кнопка осталась бы серой до перезагрузки страницы.
    try {
      const response = await send('PUT', '/restaurant', {
        ...form, tables, autoWriteOffAfterHours: form.autoWriteOff ? grace : form.autoWriteOffAfterHours,
      });
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
        <div className={`g-field${tried && tablesError ? ' g-field--error' : ''}`}>
          <label htmlFor="r-tables">Столов в зале</label>
          <input className="g-input num" id="r-tables" inputMode="numeric" value={text.tables} onChange={(e) => setText({ ...text, tables: e.target.value })} aria-invalid={tried && tablesError} aria-describedby={tried && tablesError ? 'r-tables-e' : undefined} />
          {tried && tablesError && <span className="g-error-text" id="r-tables-e">Укажите целое число столов от 0 до 60.</span>}
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
          <div className={`g-field${tried && graceError ? ' g-field--error' : ''}`}>
            <label htmlFor="r-grace">Через сколько часов после истечения срока</label>
            <div className="g-unit rest-hours">
              <input className="g-input num" id="r-grace" inputMode="numeric" value={text.grace} onChange={(e) => setText({ ...text, grace: e.target.value })} aria-invalid={tried && graceError} aria-describedby={tried && graceError ? 'r-grace-e' : undefined} />
              <span>ч</span>
            </div>
            {tried && graceError && <span className="g-error-text" id="r-grace-e">Укажите целое число часов от 1 до 168.</span>}
          </div>
        )}
        <span className="g-hint">
          {form.autoWriteOff
            ? 'Просрочка ждёт ручного списания это время (но не дольше, чем продукт хранился), потом система спишет её сама. От 1 до 168 часов.'
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
