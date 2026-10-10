import { useState } from 'react';
import { Link } from 'react-router-dom';
import { post, useApi, type PrepResult, type PrepRow } from '../api';
import { dueLong, duration, qty, shelfLife, unitName } from '../format';
import { StockTabs } from './Stock';

const number = (text: string) => Number(text.replace(',', '.'));

type Outcome = { kind: 'ok'; result: PrepResult } | { kind: 'short'; result: PrepResult } | { kind: 'err'; text: string };

/** План заготовок: что кухня готовит сама, на сколько хватит и сколько приготовить сейчас. */
export function Preps() {
  const preps = useApi<PrepRow[]>('/preps');
  const [amounts, setAmounts] = useState<Record<string, string>>({});
  const [outcomes, setOutcomes] = useState<Record<string, Outcome>>({});
  const [busy, setBusy] = useState(false);

  if (preps.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить заготовки</h3><span>{preps.error}</span></div>;
  if (!preps.data) return <p className="muted">Загружаем заготовки…</p>;

  const make = async (prep: PrepRow, quantity: number) => {
    setBusy(true);
    const response = await post('/preps', { productId: prep.productId, quantity });
    const outcome: Outcome = response.ok
      ? { kind: 'ok', result: await response.json() }
      : response.status === 409
        ? { kind: 'short', result: await response.json() }
        : { kind: 'err', text: `Сервер ответил ошибкой ${response.status}.` };
    setOutcomes((o) => ({ ...o, [prep.productId]: outcome }));
    if (response.ok) setAmounts((a) => ({ ...a, [prep.productId]: '' }));
    await preps.reload();
    setBusy(false);
  };

  return (
    <>
      <div className="g-title">
        <div className="stock-head">
          <h1>Склад</h1>
          <StockTabs />
        </div>
      </div>
      <p className="lead-text">
        Заготовки кухня делает сама. Приготовление списывает сырьё из партий с ближайшим сроком, а заготовка встаёт на склад отдельной партией со своим сроком годности.
      </p>

      {preps.data.length === 0 ? (
        <p className="g-empty"><strong>Заготовок пока нет</strong>Чтобы продукт стал заготовкой, укажите его состав в справочнике продуктов.</p>
      ) : (
        <div className="preps">
          {preps.data.map((p) => {
            const text = amounts[p.productId] ?? (p.suggested > 0 ? String(Math.min(p.suggested, p.maxQuantity)).replace('.', ',') : '');
            const quantity = number(text);
            const valid = quantity > 0 && quantity <= p.maxQuantity;
            const outcome = outcomes[p.productId];
            return (
              <section key={p.productId} className="g-panel prep" aria-label={p.productName}>
                <div className="g-panel__head">
                  <h2>{p.productName}</h2>
                  <span className="end">хранится {shelfLife(p.shelfLifeHours)}</span>
                </div>
                <dl className="prep__facts">
                  <div><dt>Годный остаток</dt><dd className="num" style={{ color: p.fresh > 0 ? undefined : 'var(--red)' }}>{qty(p.fresh, p.unit)}</dd></div>
                  <div><dt>Расход в день</dt><dd className="num">{p.perDay > 0 ? qty(p.perDay, p.unit) : '—'}</dd></div>
                  <div><dt>Хватит на</dt><dd className="num">{p.hoursCovered === null ? 'больше 3 дней' : p.hoursCovered > 0 ? duration(p.hoursCovered) : 'нет остатка'}</dd></div>
                  <div><dt>Стоит приготовить</dt><dd className="num">{p.suggested > 0 ? qty(p.suggested, p.unit) : 'пока не нужно'}</dd></div>
                </dl>
                {p.reason && <p className="muted small prep__why">{p.reason}</p>}

                <div className="g-colhead prep__cols" aria-hidden="true"><span>Состав на 1 {unitName(p.unit)}</span><span className="r">Нужно</span><span className="r">Есть годного</span></div>
                <ul className="prep__list">
                  {p.ingredients.map((i) => {
                    const need = (quantity > 0 ? quantity : 1) * i.quantity;
                    return (
                      <li key={i.productId} className="prep__cols">
                        <span className="g-name">{i.productName}</span>
                        <span className="num r">{qty(Math.round(need * 1000) / 1000, i.unit)}</span>
                        <span className="num r" style={{ color: i.fresh < need ? 'var(--red)' : undefined }}>{qty(i.fresh, i.unit)}</span>
                      </li>
                    );
                  })}
                </ul>

                {outcome?.kind === 'ok' && (
                  <div className="g-note g-note--ok prep__note" role="status">
                    <span>
                      Приготовлено {qty(outcome.result.quantity, p.unit)}, партия {outcome.result.supplyNumber}, годна до {dueLong(outcome.result.expiresAt)}.{' '}
                      <Link to={`/app/labels?batch=${outcome.result.batchId}`}>Напечатать этикетку</Link>
                    </span>
                  </div>
                )}
                {outcome?.kind === 'short' && (
                  <div className="g-note g-note--err prep__note" role="alert">
                    <span>Не хватает сырья: {outcome.result.shortages.map((s) => `${s.productName.toLowerCase()} — нужно ${qty(s.required, s.unit)}, есть ${qty(s.available, s.unit)}`).join('; ')}. Ничего не списано.</span>
                  </div>
                )}
                {outcome?.kind === 'err' && <div className="g-note g-note--err prep__note" role="alert"><span>{outcome.text}</span></div>}

                <form className="g-panel__foot prep__foot" onSubmit={(e) => { e.preventDefault(); if (valid) void make(p, quantity); }}>
                  <span className="muted small">из сырья выйдет до {qty(p.maxQuantity, p.unit)}</span>
                  <div className="g-unit prep__amount">
                    <label className="sr" htmlFor={`prep-${p.productId}`}>Сколько приготовить</label>
                    <input className="g-input num" id={`prep-${p.productId}`} inputMode="decimal" placeholder="Сколько" value={text} onChange={(e) => setAmounts((a) => ({ ...a, [p.productId]: e.target.value }))} />
                    <span>{unitName(p.unit)}</span>
                  </div>
                  <button className="g-btn g-btn--primary" type="submit" disabled={busy || !valid}>Приготовить</button>
                </form>
              </section>
            );
          })}
        </div>
      )}
    </>
  );
}
