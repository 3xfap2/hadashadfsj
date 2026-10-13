import { Fragment, useRef, useState, type FormEvent } from 'react';
import { get, post, useApi, type InventoryResult, type InventorySummary, type StockRow } from '../api';
import { dueLong, money, qty, unitName } from '../format';
import { StockTabs } from './Stock';

const number = (text: string) => Number(text.replace(',', '.'));

/** Инвентаризация: сотрудник пересчитывает склад и вводит остаток по факту, система исправляет учёт и показывает расхождение. */
export function Inventory() {
  const stock = useApi<StockRow[]>('/stock');
  // Заполняются только пересчитанные продукты: пустое поле значит «не считали», остаток не меняется.
  const [actual, setActual] = useState<Record<string, string>>({});
  const [result, setResult] = useState<InventoryResult | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  if (stock.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить склад</h3><span>{stock.error}</span></div>;
  if (!stock.data) return <p className="muted">Загружаем склад…</p>;

  const counted = stock.data.filter((s) => (actual[s.productId] ?? '').trim() !== '');
  const invalid = counted.filter((s) => !(number(actual[s.productId]) >= 0));
  const differ = counted.filter((s) => !invalid.includes(s) && Math.round((number(actual[s.productId]) - s.fresh - s.expired) * 1000) !== 0);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (counted.length === 0 || invalid.length > 0) return;
    setBusy(true);
    setError('');
    try {
      const response = await post('/stock/inventory', { items: counted.map((s) => ({ productId: s.productId, actual: number(actual[s.productId]) })) });
      if (response.ok) {
        setResult(await response.json());
        setActual({});
        await stock.reload();
      } else {
        const problem = await response.json().catch(() => ({}));
        setError(Object.values((problem.errors ?? {}) as Record<string, string[]>).flat().join(' ') || `Сервер ответил ошибкой ${response.status}.`);
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <div className="g-title">
        <div className="stock-head">
          <h1>Склад</h1>
          <StockTabs />
        </div>
      </div>

      {result && (
        <section className={`g-note ${result.rows.length === 0 ? 'g-note--ok' : 'g-note--err'}`} role="status">
          <h3>{result.rows.length === 0 ? `Инвентаризация № ${result.number}: расхождений нет` : `Инвентаризация № ${result.number} проведена, остатки исправлены`}</h3>
          {result.rows.length > 0 && (
            <>
              <span className="hd">
                Недостача <span className="num">{money(result.shortage)}</span> · излишек <span className="num">{money(result.surplus)}</span>
              </span>
              <ul className="orders__lack">
                {result.rows.map((r) => (
                  <li key={r.productId}>
                    <strong className="hd">{r.productName}:</strong> по учёту <span className="num">{qty(r.accounted, r.unit)}</span>, по факту{' '}
                    <span className="num">{qty(r.actual, r.unit)}</span> · <span className="num">{r.value > 0 ? '+' : '−'}{money(Math.abs(r.value))}</span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </section>
      )}

      <form className="g-panel" onSubmit={submit} noValidate>
        <div className="g-panel__head">
          <h2>Инвентаризация</h2>
          <span className="end">введите остаток по факту у тех продуктов, которые пересчитали</span>
        </div>
        {stock.data.length === 0 ? (
          <p className="g-empty"><strong>Продуктов пока нет</strong>Они появятся после первой поставки.</p>
        ) : (
          <div className="g-table-wrap">
            <table className="g-table inv">
              <thead><tr><th>Продукт</th><th className="r">По учёту</th><th>По факту</th><th className="r">Расхождение</th></tr></thead>
              <tbody>
                {stock.data.map((s) => {
                  const accounted = s.fresh + s.expired;
                  const text = actual[s.productId] ?? '';
                  const bad = invalid.includes(s);
                  const diff = text.trim() === '' || bad ? null : Math.round((number(text) - accounted) * 1000) / 1000;
                  return (
                    <tr key={s.productId}>
                      <td className="g-name">{s.productName}{s.expired > 0 && <span className="muted small"> · из них просрочено {qty(s.expired, s.unit)}</span>}</td>
                      <td className="r num">{qty(accounted, s.unit)}</td>
                      <td>
                        <div className="g-unit inv__in">
                          <input
                            className="g-input num" inputMode="decimal" aria-label={`${s.productName}: остаток по факту`} aria-invalid={bad}
                            value={text} onChange={(e) => setActual({ ...actual, [s.productId]: e.target.value })}
                          />
                          <span>{unitName(s.unit)}</span>
                        </div>
                      </td>
                      <td className="r num hd">{diff === null || diff === 0 ? <span className="muted">{diff === 0 ? 'сходится' : '—'}</span> : `${diff > 0 ? '+' : '−'}${qty(Math.abs(diff), s.unit)}`}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <div className="g-panel__foot sup-foot">
          <span>
            <span className="muted">Пересчитано:</span> <strong className="num hd">{counted.length}</strong> · <span className="muted">с расхождением:</span>{' '}
            <strong className="num hd">{differ.length}</strong>
            {invalid.length > 0 && <span className="g-error-text"> · проверьте числа в отмеченных полях</span>}
            {error && <span className="g-error-text" role="alert"> · {error}</span>}
          </span>
          <button className="g-btn g-btn--primary" type="submit" disabled={busy || counted.length === 0 || invalid.length > 0}>
            {busy ? 'Проводим…' : 'Провести инвентаризацию'}
          </button>
        </div>
      </form>
      <p className="muted small">Недостача уходит из партий с самым ранним сроком, излишек добавляется к самой свежей. Обе операции видны в журнале.</p>
      <History />
    </>
  );
}

/** Прошлые инвентаризации: кто и когда пересчитывал склад и что нашёл; строки документа раскрываются по нажатию. */
function History() {
  const list = useApi<InventorySummary[]>('/stock/inventories', 60_000);
  const [openId, setOpenId] = useState<string | null>(null);
  const [doc, setDoc] = useState<InventoryResult | 'failed' | null>(null);
  // Какой документ открыт сейчас: ответ на прежний клик не должен попасть в другую строку.
  const wanted = useRef<string | null>(null);

  const toggle = async (id: string) => {
    setDoc(null);
    wanted.current = openId === id ? null : id;
    setOpenId(wanted.current);
    if (!wanted.current) return;
    const loaded = await get<InventoryResult>(`/stock/inventories/${id}`).catch(() => 'failed' as const);
    if (wanted.current === id) setDoc(loaded);
  };

  if (!list.data) return null;

  return (
    <section className="g-panel" aria-labelledby="inv-past-h">
      <div className="g-panel__head"><h2 id="inv-past-h">Прошлые инвентаризации</h2></div>
      {list.data.length === 0 ? (
        <p className="g-empty"><strong>Пересчётов пока не было</strong>Каждая проведённая инвентаризация сохранится здесь документом.</p>
      ) : (
        <div className="g-table-wrap">
          <table className="g-table inv-past">
            <thead><tr><th>№</th><th>Когда</th><th>Кто провёл</th><th className="r">Пересчитано</th><th className="r">Недостача</th><th className="r">Излишек</th><th /></tr></thead>
            <tbody>
              {list.data.map((i) => (
                <Fragment key={i.id}>
                  <tr>
                    <td className="num hd">{i.number}</td>
                    <td className="num">{dueLong(i.at)}</td>
                    <td>{i.actor}</td>
                    <td className="r num">{i.counted}<span className="muted small"> · расхождений {i.differences}</span></td>
                    <td className="r num hd">{i.shortage > 0 ? `−${money(i.shortage)}` : '—'}</td>
                    <td className="r num">{i.surplus > 0 ? `+${money(i.surplus)}` : '—'}</td>
                    <td className="r"><button className="g-btn g-btn--sm" type="button" aria-expanded={openId === i.id} onClick={() => toggle(i.id)}>{openId === i.id ? 'Скрыть' : 'Строки'}</button></td>
                  </tr>
                  {openId === i.id && (
                    <tr className="inv-past__doc">
                      <td colSpan={7}>
                        {!doc ? <span className="muted">Загружаем строки…</span> : doc === 'failed' ? <span className="g-error-text" role="alert">Строки не загрузились. Закройте и откройте документ ещё раз.</span> : (
                          <ul>
                            {doc.rows.map((r) => (
                              <li key={r.productId}>
                                <strong className="hd">{r.productName}:</strong> по учёту <span className="num">{qty(r.accounted, r.unit)}</span>, по факту{' '}
                                <span className="num">{qty(r.actual, r.unit)}</span> ·{' '}
                                {r.difference === 0 ? <span className="muted">сходится</span> : <span className="num hd">{r.value > 0 ? '+' : '−'}{money(Math.abs(r.value))}</span>}
                              </li>
                            ))}
                          </ul>
                        )}
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
