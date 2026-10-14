import { useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { post, problemText, useApi, type IncomingRow, type ProductRow, type SupplyRow } from '../api';
import { dueLong, money, plural, qty, toLocalInput, unitName, weekdayLong } from '../format';
import { PlusIcon, useCan } from '../ui';
import { clearDraft, loadDraft, saveDraft, type DraftLine, type SupplyDraft } from '../supplyDraft';
import { StockTabs } from './Stock';

type Note = { kind: 'ok' | 'err'; title: string; text: string } | null;

const number = (text: string) => Number(text.replace(',', '.'));

export function Supplies() {
  const products = useApi<ProductRow[]>('/products', 300_000);
  const supplies = useApi<SupplyRow[]>('/supplies?take=200');
  const incoming = useApi<IncomingRow[]>('/supplies/incoming');
  const decided = useApi<IncomingRow[]>('/supplies/incoming/history?take=200');
  const [draft, setDraft] = useState<SupplyDraft | null>(null);
  const [savedDraft, setSavedDraft] = useState(() => loadDraft());
  const [tried, setTried] = useState(false);
  const [note, setNote] = useState<Note>(null);
  const [busy, setBusy] = useState(false);
  // Справочник открыт не всем ролям: кладовщику ссылка на него вела бы обратно на «Сегодня».
  const canCatalog = useCan('catalog');

  // Форма открывается с сохранённым черновиком, а если его нет — с одной пустой строкой и следующим номером.
  useEffect(() => {
    if (draft || !products.data || !supplies.data || products.data.length === 0) return;
    // В черновике могли остаться продукты, которых в справочнике уже нет: такие строки отбрасываются.
    const known = savedDraft && { ...savedDraft, lines: savedDraft.lines.filter((l) => products.data!.some((p) => p.id === l.productId)) };
    setDraft(known && known.lines.length > 0 ? known : { number: nextNumber(supplies.data), date: '', supplier: '', lines: [emptyLine(products.data[0])] });
  }, [draft, savedDraft, products.data, supplies.data]);

  const error = products.error ?? supplies.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить поставки</h3><span>{error}</span></div>;
  if (products.data?.length === 0) {
    return (
      <>
        <div className="g-title"><div className="stock-head"><h1>Склад</h1><StockTabs /></div></div>
        <p className="g-empty">
          <strong>Сначала заведите продукты</strong>
          Поставка состоит из продуктов справочника.{' '}
          {canCatalog ? <Link to="/app/catalog">Открыть справочник продуктов</Link> : 'Попросите шеф-повара или управляющего завести их.'}
        </p>
      </>
    );
  }
  if (!draft || !products.data || !supplies.data) return <p className="muted">Загружаем поставки…</p>;

  const byId = (id: string) => products.data!.find((p) => p.id === id)!;
  const reloadAll = async () => {
    await Promise.all([incoming.reload(), supplies.reload(), decided.reload()]);
  };
  // Решения по поставкам извне: по принятой видно «заказано / принято», отклонённая остаётся в истории, хотя на склад ничего не добавила.
  const history = new Map((decided.data ?? []).map((d) => [d.id, d]));
  const rejected = (decided.data ?? []).filter((d) => d.status === 'Rejected');
  const received = draft.date ? new Date(draft.date) : new Date();
  const update = (patch: Partial<SupplyDraft>) => setDraft({ ...draft, ...patch });
  const updateLine = (index: number, patch: Partial<DraftLine>) =>
    update({ lines: draft.lines.map((l, i) => (i === index ? { ...l, ...patch } : l)) });

  const badQty = (l: DraftLine) => !(number(l.quantity) > 0);
  const badLife = (l: DraftLine) => !(number(l.life) > 0);
  const total = draft.lines.reduce((sum, l) => sum + (number(l.quantity) || 0) * (byId(l.productId).cost ?? 0), 0);
  const weekAgo = Date.now() - 7 * 86_400_000;

  const keepDraft = () => {
    saveDraft(draft);
    setSavedDraft(draft);
    setNote({ kind: 'ok', title: 'Черновик сохранён', text: 'Он хранится на этом устройстве и откроется здесь же, когда вы вернётесь.' });
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    setNote(null);
    if (!draft.number.trim() || draft.lines.length === 0 || draft.lines.some((l) => badQty(l) || badLife(l))) return;
    setBusy(true);
    try {
      const response = await post('/supplies', {
        number: draft.number.trim(),
        supplier: draft.supplier.trim() || null,
        // Дата не тронута — сервер поставит текущее время сам.
        createdAt: draft.date ? new Date(draft.date).toISOString() : null,
        items: draft.lines.map((l) => ({ productId: l.productId, quantity: number(l.quantity), shelfLifeHours: Math.round(number(l.life)) })),
      });
      if (response.status === 201) {
        const created: SupplyRow = await response.json();
        clearDraft();
        setSavedDraft(null);
        setTried(false);
        setNote({ kind: 'ok', title: `Поставка ${created.number} принята`, text: `На склад добавлено: ${plural(created.items.length, 'партия', 'партии', 'партий')} на ${money(created.total ?? 0)}.` });
        await supplies.reload();
        setDraft({ number: bump(created.number), date: '', supplier: '', lines: [emptyLine(products.data![0])] });
      } else if (response.status === 409) {
        setNote({ kind: 'err', title: 'Поставка не принята', text: 'Поставка с таким номером уже есть. Укажите другой номер.' });
      } else if (response.status === 400) {
        const problem = await response.json().catch(() => ({}));
        const messages = Object.values((problem.errors ?? {}) as Record<string, string[]>).flat();
        setNote({ kind: 'err', title: 'Поставка не принята', text: messages.join(' ') || 'Проверьте поля формы.' });
      } else {
        setNote({ kind: 'err', title: 'Поставка не принята', text: `Сервер ответил ошибкой ${response.status}.` });
      }
    } catch (e) {
      setNote({ kind: 'err', title: 'Поставка не принята', text: (e as Error).message });
    }
    setBusy(false);
  };

  return (
    <>
      <div className="sup-head">
        <div className="stock-head">
          <h1>Склад</h1>
          <StockTabs />
        </div>
        <Link className="g-btn" to="/app/stock/import">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V4M7 9l5-5 5 5M5 20h14" /></svg>
          Загрузить из файла
        </Link>
      </div>

      <div className="sup-row">
        <section className="sup-main" aria-labelledby="new-h">
          <form className="g-panel" onSubmit={submit} noValidate>
            <div className="g-panel__head sup-form-head">
              <h2 id="new-h">Новая поставка</h2>
              <span className="end">срок годности считается по нормативу хранения</span>
            </div>
            <div className="sup-body">
              <div className="sup-fields">
                <div className={`g-field${tried && !draft.number.trim() ? ' g-field--error' : ''}`}>
                  <label htmlFor="s-no">Номер поставки</label>
                  <input className="g-input num" id="s-no" value={draft.number} onChange={(e) => update({ number: e.target.value })} />
                  {tried && !draft.number.trim() && <span className="g-error-text">Укажите номер, например П-0422</span>}
                </div>
                <div className="g-field">
                  <label htmlFor="s-date">Дата и время приёмки</label>
                  <input className="g-input num" id="s-date" type="datetime-local" max={toLocalInput(Date.now())} value={draft.date || toLocalInput(Date.now())} onChange={(e) => update({ date: e.target.value })} />
                </div>
                <div className="g-field">
                  <label htmlFor="s-sup">Поставщик</label>
                  <input className="g-input" id="s-sup" value={draft.supplier} onChange={(e) => update({ supplier: e.target.value })} />
                </div>
              </div>

              <div>
                <div className="line hdr" aria-hidden="true">
                  <span>Продукт</span><span>Количество</span><span>Срок хранения</span><span>Годен до</span><span />
                </div>
                {draft.lines.map((l, i) => {
                  const product = byId(l.productId);
                  const until = new Date(received.getTime() + (number(l.life) || 0) * 3_600_000);
                  const qtyError = tried && badQty(l);
                  return (
                    <div className="line" key={i}>
                      <div className="g-field wide">
                        <label className="sr" htmlFor={`p${i}`}>Продукт, строка {i + 1}</label>
                        <select
                          className="g-input"
                          id={`p${i}`}
                          value={l.productId}
                          onChange={(e) => updateLine(i, { productId: e.target.value, life: String(byId(e.target.value).shelfLifeHours) })}
                        >
                          {products.data!.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                        </select>
                      </div>
                      <div className={`g-field${qtyError ? ' g-field--error' : ''}`}>
                        <label className="sr" htmlFor={`q${i}`}>Количество, строка {i + 1}</label>
                        <div className="g-unit">
                          <input className="g-input num" id={`q${i}`} inputMode="decimal" value={l.quantity} onChange={(e) => updateLine(i, { quantity: e.target.value })} aria-invalid={qtyError} aria-describedby={qtyError ? `e${i}` : undefined} />
                          <span>{unitName(product.unit)}</span>
                        </div>
                        {qtyError && <span className="g-error-text" id={`e${i}`}>Укажите количество, например 2,5</span>}
                      </div>
                      <div className={`g-field${tried && badLife(l) ? ' g-field--error' : ''}`}>
                        <label className="sr" htmlFor={`l${i}`}>Срок хранения, строка {i + 1}</label>
                        <div className="g-unit">
                          <input className="g-input num" id={`l${i}`} inputMode="numeric" value={l.life} onChange={(e) => updateLine(i, { life: e.target.value })} />
                          <span>ч</span>
                        </div>
                        <span className="g-hint">норматив: {product.shelfLifeHours} ч</span>
                      </div>
                      <div className="line__until">
                        <strong className="num hd">{dueLong(until.toISOString())}</strong>
                        <span className="muted small">{weekdayLong(until.toISOString())}</span>
                      </div>
                      <button className="icon-btn" type="button" aria-label={`Удалить строку ${i + 1}`} onClick={() => update({ lines: draft.lines.filter((_, j) => j !== i) })}>
                        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 7h14M10 7V4h4v3M7 7l1 13h8l1-13" /></svg>
                      </button>
                    </div>
                  );
                })}
                {tried && draft.lines.length === 0 && <p className="g-error-text">Добавьте хотя бы один продукт.</p>}
                <button className="g-btn g-btn--sm sup-add" type="button" onClick={() => update({ lines: [...draft.lines, emptyLine(products.data![0])] })}>
                  <PlusIcon />Добавить продукт
                </button>
              </div>

              {note && (
                <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}>
                  <h3>{note.title}</h3>
                  <span>{note.text}</span>
                </div>
              )}
            </div>
            <div className="g-panel__foot sup-foot">
              <span>
                <span className="muted">Позиций:</span> <strong className="num hd">{draft.lines.length}</strong> · <span className="muted">на сумму</span>{' '}
                <strong className="num hd">{money(total)}</strong>
              </span>
              <div className="sup-actions">
                <button className="g-btn" type="button" onClick={keepDraft}>Сохранить черновик</button>
                <button className="g-btn g-btn--primary" type="submit" disabled={busy}>{busy ? 'Принимаем…' : 'Принять поставку'}</button>
              </div>
            </div>
          </form>
        </section>

        <aside className="sup-side" aria-labelledby="list-h">
          <div className="sup-side__head">
            <h2 id="list-h">Поставки</h2>
            <span className="muted small">за неделю · {supplies.data.filter((s) => new Date(s.createdAt).getTime() >= weekAgo).length}</span>
          </div>
          <ul className="sup-list">
            {incoming.data?.map((s) => <IncomingCard key={s.id} supply={s} onDone={reloadAll} />)}
            {savedDraft && (
              <li className="g-panel sup">
                <div className="sup__top"><span className="g-name num">{savedDraft.number || 'Без номера'}</span><span className="num muted small">на этом устройстве</span></div>
                {savedDraft.supplier && <span className="muted small">{savedDraft.supplier}</span>}
                <span className="sup__items">Черновик · {plural(savedDraft.lines.length, 'позиция', 'позиции', 'позиций')}</span>
                <div className="sup__bottom">
                  {/* Продукта из черновика может уже не быть в справочнике: такие строки в сумму не входят. */}
                  <strong className="num hd">{money(savedDraft.lines.reduce((sum, l) => sum + (number(l.quantity) || 0) * (products.data!.find((p) => p.id === l.productId)?.cost ?? 0), 0))}</strong>
                  <span className="g-tag g-tag--amber">Черновик</span>
                </div>
              </li>
            )}
            {supplies.data.length === 0 && !savedDraft && !incoming.data?.length && <li className="g-empty"><strong>Поставок пока нет</strong>Первая появится здесь после приёмки.</li>}
            {rejected.map((d) => (
              <li key={d.id} className="g-panel sup sup--rejected">
                <div className="sup__top"><span className="g-name num">{d.number}</span><span className="num muted small">{dueLong(d.decidedAt ?? d.receivedAt)}</span></div>
                <SupplierLine supply={d} />
                <span className="sup__items">{d.items.map((i) => `${i.productName} ${qty(i.quantity, i.unit)}`).join(' · ')}</span>
                <div className="sup__bottom"><span className="muted small">На склад ничего не добавлено{d.decidedBy ? ` · ${d.decidedBy}` : ''}</span><span className="g-tag g-tag--red">Отклонена</span></div>
              </li>
            ))}
            {supplies.data.map((s) => {
              const from = history.get(s.id);
              return (
                <li key={s.id} className="g-panel sup">
                  <div className="sup__top"><span className="g-name num">{s.number}</span><span className="num muted small">{dueLong(s.createdAt)}</span></div>
                  {from ? <SupplierLine supply={from} /> : s.supplier && <span className="muted small">{s.supplier}</span>}
                  {from?.status === 'Partial' ? (
                    <ul className="sup__lines">
                      {from.items.map((i) => (
                        <li key={i.id} className={i.accepted === i.quantity ? '' : 'short'}>
                          <span>{i.productName}</span>
                          <span className="num">{qty(i.accepted ?? 0, i.unit)} из {qty(i.quantity, i.unit)}</span>
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <span className="sup__items">{s.items.map((i) => `${i.productName} ${qty(i.quantity, i.unit)}`).join(' · ')}</span>
                  )}
                  <div className="sup__bottom">
                    <strong className="num hd">{money(s.total ?? 0)}</strong>
                    <span className="muted small">
                      {from?.status === 'Partial' ? <span className="g-tag g-tag--amber">Принята частично</span> : 'Принята'} · <Link to={`/app/labels?supply=${encodeURIComponent(s.number)}`}>этикетки</Link>
                    </span>
                  </div>
                </li>
              );
            })}
          </ul>
        </aside>
      </div>
    </>
  );
}

function SupplierLine({ supply }: { supply: IncomingRow }) {
  return (
    <span className="muted small">
      {supply.supplier ?? 'Поставщик не указан'}
      {supply.supplierInn && <> · ИНН <span className="num">{supply.supplierInn}</span></>}
    </span>
  );
}

/**
 * Поставка из внешней системы, которая ждёт решения: принять всё, принять частично (по каждой строке — сколько принято,
 * 0 — строка не принята) или отклонить. На склад встаёт только принятое.
 */
function IncomingCard({ supply, onDone }: { supply: IncomingRow; onDone: () => Promise<void> }) {
  const [accepted, setAccepted] = useState<Record<number, string> | null>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  const bad = (line: IncomingRow['items'][number]) => {
    const value = accepted ? number(accepted[line.id] ?? '') : line.quantity;
    return !(value >= 0 && value <= line.quantity);
  };

  const decide = async (action: 'accept' | 'reject', body?: unknown) => {
    setState({ busy: true, error: '' });
    let error = '';
    try {
      const response = await post(`/supplies/incoming/${supply.id}/${action}`, body);
      if (!response.ok) error = await problemText(response);
      await onDone();
    } catch (e) {
      error = (e as Error).message;
    }
    setState({ busy: false, error });
  };
  const acceptPartly = () => {
    if (!accepted || supply.items.some(bad)) return;
    void decide('accept', { items: supply.items.map((i) => ({ id: i.id, accepted: number(accepted[i.id]) })) });
  };

  return (
    <li className="g-panel sup sup--waiting">
      <div className="sup__top"><span className="g-name num">{supply.number}</span><span className="num muted small">от {dueLong(supply.createdAt)}</span></div>
      <SupplierLine supply={supply} />
      <ul className="sup__lines">
        {supply.items.map((i) => (
          <li key={i.id}>
            <span>{i.productName} <span className="muted small">· срок {i.shelfLifeHours} ч</span></span>
            {accepted ? (
              <span className={`g-field sup__accept${bad(i) ? ' g-field--error' : ''}`}>
                <label className="sr" htmlFor={`acc-${i.id}`}>Принято: {i.productName}, заказано {qty(i.quantity, i.unit)}</label>
                <span className="g-unit">
                  <input
                    className="g-input num"
                    id={`acc-${i.id}`}
                    inputMode="decimal"
                    value={accepted[i.id] ?? ''}
                    aria-invalid={bad(i)}
                    onChange={(e) => setAccepted({ ...accepted, [i.id]: e.target.value })}
                  />
                  <span>из {qty(i.quantity, i.unit)}</span>
                </span>
              </span>
            ) : (
              <span className="num">{qty(i.quantity, i.unit)}</span>
            )}
          </li>
        ))}
      </ul>
      <div className="sup__bottom"><strong className="num hd">{money(supply.total)}</strong><span className="g-tag g-tag--amber">Ждёт подтверждения</span></div>
      <span className="muted small">
        {accepted
          ? 'Укажите, сколько принято по каждой строке: 0 — строку не берёте. На склад встанет только принятое.'
          : `Пришла из внешней системы ${dueLong(supply.receivedAt)}. Срок годности считается от даты поставки, на склад она попадёт после приёмки.`}
      </span>
      {accepted && supply.items.some(bad) && <span className="g-error-text">Принятое количество — от 0 до заказанного.</span>}
      {state.error && <span className="g-error-text" role="alert">{state.error}</span>}
      <div className="sup__decide">
        {accepted ? (
          <>
            <button className="g-btn g-btn--sm" type="button" disabled={state.busy} onClick={() => setAccepted(null)}>Отмена</button>
            <button className="g-btn g-btn--sm g-btn--primary" type="button" disabled={state.busy} onClick={acceptPartly}>{state.busy ? 'Принимаем…' : 'Принять указанное'}</button>
          </>
        ) : (
          <>
            <button className="g-btn g-btn--sm" type="button" disabled={state.busy} onClick={() => decide('reject')}>Отклонить</button>
            <button className="g-btn g-btn--sm" type="button" disabled={state.busy} onClick={() => setAccepted(Object.fromEntries(supply.items.map((i) => [i.id, String(i.quantity).replace('.', ',')])))}>
              Принять частично
            </button>
            <button className="g-btn g-btn--sm g-btn--primary" type="button" disabled={state.busy} onClick={() => decide('accept')}>Принять всё</button>
          </>
        )}
      </div>
    </li>
  );
}

const emptyLine = (product: ProductRow): DraftLine => ({ productId: product.id, quantity: '', life: String(product.shelfLifeHours) });

/** «П-0412» → «П-0413»: увеличивает число в конце номера, сохраняя длину. */
function bump(number: string): string {
  const match = /^(.*?)(\d+)$/.exec(number);
  return match ? match[1] + String(Number(match[2]) + 1).padStart(match[2].length, '0') : '';
}

/** Следующий номер после самого большого из принятых — подсказка, которую можно исправить. */
function nextNumber(supplies: SupplyRow[]): string {
  const numbered = supplies.map((s) => s.number).filter((n) => /\d+$/.test(n)).sort((a, b) => Number(/\d+$/.exec(b)![0]) - Number(/\d+$/.exec(a)![0]));
  return numbered.length > 0 ? bump(numbered[0]) : '';
}
