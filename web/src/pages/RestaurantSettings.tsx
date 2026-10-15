import { useEffect, useState, type FormEvent } from 'react';
import { post, problemText, send, useApi, type RestaurantSettings as Settings } from '../api';
import { clock, dayMonthShort } from '../format';

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
      <IntegrationKeys />
    </form>
  );
}

type Right = 'supplies' | 'orders';

/** Ключ внешней системы в списке; сам ключ виден только при выдаче. */
interface ApiKeyRow {
  id: string;
  name: string;
  rights: Right[];
  createdAt: string;
  createdBy: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
  /** В демо-ресторане ключ отзывается только в первые 30 минут после выдачи. */
  canRevoke: boolean;
}

const RIGHTS: Record<Right, string> = { supplies: 'Поставки', orders: 'Заказы и меню' };
const shortDate = (iso: string) => `${dayMonthShort(iso)}, ${clock(iso)}`;

/**
 * Ключи внешних систем: у каждой системы свой ключ со своими правами — поставщик присылает поставки, касса или сайт кафе — заказы.
 * Ключ показывается один раз; отозванный ключ сразу перестаёт работать, остальные не затрагиваются.
 */
function IntegrationKeys() {
  const keys = useApi<ApiKeyRow[]>('/restaurant/keys', 300_000);
  const [draft, setDraft] = useState<{ name: string; rights: Right[] }>({ name: '', rights: ['orders'] });
  const [issued, setIssued] = useState<{ key: string; name: string } | null>(null);
  const [state, setState] = useState({ busy: false, error: '', copied: false });

  const run = async (action: () => Promise<Response>, done: (response: Response) => Promise<void>) => {
    setState({ busy: true, error: '', copied: false });
    try {
      const response = await action();
      if (response.ok) {
        await done(response);
        setState({ busy: false, error: '', copied: false });
        await keys.reload();
      } else {
        setState({ busy: false, error: await problemText(response), copied: false });
      }
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, copied: false });
    }
  };
  const issue = () =>
    run(() => post('/restaurant/keys', draft), async (response) => {
      const created = (await response.json()) as { key: string; name: string };
      setIssued({ key: created.key, name: created.name });
      setDraft({ name: '', rights: ['orders'] });
    });
  const revoke = (row: ApiKeyRow) => {
    if (!window.confirm(`Отозвать ключ «${row.name}»? Система с этим ключом сразу перестанет работать.`)) return;
    void run(() => post(`/restaurant/keys/${row.id}/revoke`), async () => setIssued(null));
  };
  const toggle = (right: Right) =>
    setDraft((d) => ({ ...d, rights: d.rights.includes(right) ? d.rights.filter((r) => r !== right) : [...d.rights, right] }));
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(issued?.key ?? '');
      setState((s) => ({ ...s, copied: true }));
    } catch {
      // Буфер обмена недоступен (нет разрешения): ключ виден на экране, его можно выделить и скопировать вручную.
    }
  };

  return (
    <div className="form-body rest-key">
      <h3>Ключи внешних систем</h3>
      <span className="muted">
        У каждой системы свой ключ в заголовке <code>X-Api-Key</code>. С правом «Поставки» сервис поставщика присылает поставку
        (<code>POST /api/supplies/incoming</code>) — она встанет на склад после приёмки. С правом «Заказы и меню» касса или сайт кафе
        видят меню со стоп-листом и оформляют заказ (<code>POST /api/orders/external</code>) — он сразу уходит на кухню.
        Цены закупки и остатки ключу не видны.
      </span>
      {issued && (
        <div className="g-note g-note--ok" role="status">
          <h3>Ключ «{issued.name}» выдан — скопируйте его сейчас</h3>
          <span>Он показывается один раз: у нас хранится только его отпечаток.</span>
          <code className="rest-key__value">{issued.key}</code>
          <span className="rest-key__acts">
            <button className="g-btn g-btn--sm" type="button" onClick={copy}>{state.copied ? 'Скопировано' : 'Скопировать'}</button>
          </span>
        </div>
      )}
      {keys.data && keys.data.length > 0 && (
        <ul className="rest-keys" aria-label="Выданные ключи">
          {keys.data.map((k) => (
            <li key={k.id} className={k.revokedAt ? 'rest-keys__off' : undefined}>
              <span className="rest-keys__main">
                <strong className="g-name">{k.name}</strong>
                <span className="rest-keys__rights">
                  {k.rights.map((r) => <span key={r} className="g-tag rest-keys__right">{RIGHTS[r]}</span>)}
                </span>
              </span>
              <span className="muted small rest-keys__meta">
                {k.revokedAt
                  ? `Отозван ${shortDate(k.revokedAt)}`
                  : `Выдан ${shortDate(k.createdAt)}${k.createdBy ? ` · ${k.createdBy}` : ''} · ${k.lastUsedAt ? `запрос ${shortDate(k.lastUsedAt)}` : 'ещё не использовался'}`}
              </span>
              {!k.revokedAt && (
                <span className="rest-keys__act">
                  {k.canRevoke
                    ? <button className="g-btn g-btn--sm" type="button" disabled={state.busy} onClick={() => revoke(k)}>Отозвать</button>
                    : <span className="muted small">ключ для показа, не отзывается</span>}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
      {keys.error && <span className="g-error-text" role="alert">{keys.error}</span>}
      <div className="rest-keys__new">
        <div className="g-field">
          <label htmlFor="k-name">Чей ключ</label>
          <input
            className="g-input" id="k-name" maxLength={60} placeholder="Касса зала, Поставщик ФрешПоставка" value={draft.name}
            onChange={(e) => setDraft({ ...draft, name: e.target.value })}
            // Поле стоит внутри формы ресторана: Enter выдаёт ключ, а не сохраняет настройки.
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); if (!state.busy) void issue(); } }}
          />
        </div>
        <fieldset className="rest-keys__checks">
          <legend className="sr">Права ключа</legend>
          {(Object.keys(RIGHTS) as Right[]).map((r) => (
            <label key={r} className="hot-check">
              <input type="checkbox" checked={draft.rights.includes(r)} onChange={() => toggle(r)} />
              <span>{RIGHTS[r]}</span>
            </label>
          ))}
        </fieldset>
        <button className="g-btn" type="button" disabled={state.busy} onClick={issue}>Выдать ключ</button>
      </div>
      {state.error && <span className="g-error-text" role="alert">{state.error}</span>}
      <span className="rest-key__acts">
        <a className="g-btn g-btn--sm" href="/supplier/" target="_blank" rel="noreferrer">Страница поставщика</a>
        <a className="g-btn g-btn--sm" href="/pos/" target="_blank" rel="noreferrer">Касса / сайт кафе</a>
      </span>
    </div>
  );
}
