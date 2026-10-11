import { useState } from 'react';
import { Link } from 'react-router-dom';
import { post, useApi, type BatchRow, type DishRow, type HotOffer, type Me, type ProductRow, type StockRow, type StopListRow } from '../api';
import { clock, dueLong, duration, money, plural, qty, recipeQty } from '../format';
import { Photo, URGENT_HOURS, useCan } from '../ui';

type Filter = 'all' | 'Available' | 'Low' | 'Stop';

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Все' },
  { id: 'Available', label: 'В продаже' },
  { id: 'Low', label: 'Заканчивается' },
  { id: 'Stop', label: 'Стоп' },
];
const CHIPS = { Available: ['plain', 'В продаже'], Low: ['g-tag g-tag--amber', 'Заканчивается'], Stop: ['g-tag g-tag--red', 'Стоп'] } as const;
const STOP_REASON = {
  Expired: 'просрочено',
  OutOfStock: 'нет на складе',
  NotEnough: 'не хватает на порцию',
  Manual: 'снято с продажи вручную',
};

export function Menu() {
  const menu = useApi<StopListRow[]>('/stop-list');
  const dishes = useApi<DishRow[]>('/dishes', 60_000);
  const products = useApi<ProductRow[]>('/products', 300_000);
  const stock = useApi<StockRow[]>('/stock');
  const batches = useApi<BatchRow[]>('/stock/batches');
  const offers = useApi<HotOffer[]>('/hot-offers');
  const me = useApi<Me>('/me', 300_000);
  const canCatalog = useCan('catalog');
  const canStop = useCan('kitchen');
  const [filter, setFilter] = useState<Filter>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const error = menu.error ?? dishes.error ?? products.error ?? stock.error ?? batches.error ?? offers.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить меню</h3><span>{error}</span></div>;
  if (!menu.data || !dishes.data || !products.data || !stock.data || !batches.data || !offers.data) return <p className="muted">Загружаем меню…</p>;

  const rows = [...menu.data].sort((a, b) => a.dishName.localeCompare(b.dishName, 'ru'));
  const shown = rows.filter((d) => filter === 'all' || d.status === filter);
  const selected = rows.find((d) => d.dishId === selectedId) ?? rows[0];

  /** Строка под числом порций: почему блюдо в таком состоянии и что с ним будет. */
  const why = (d: StopListRow): { text: string; color: string } => {
    if (d.status === 'Stop') return { text: `${d.limitingProductName}: ${STOP_REASON[d.reason!]}`, color: 'var(--red)' };
    const offer = offers.data!.find((o) => o.dishId === d.dishId)?.ingredients[0];
    if (offer && offer.hoursLeft < URGENT_HOURS)
      return { text: `${offer.productName} истекает через ${duration(offer.hoursLeft)} — продать первым`, color: 'var(--red)' };
    if (d.stopForecastAt && !lastsUntilShiftEnd(d.stopForecastAt, me.data?.shift.end))
      return { text: `Уйдёт в стоп примерно в ${clock(d.stopForecastAt)}`, color: 'var(--amber)' };
    if (offer) return { text: `${offer.productName} истекает через ${duration(offer.hoursLeft)}`, color: 'var(--amber)' };
    return { text: 'До конца смены хватит', color: 'var(--ink-2)' };
  };

  const toggleStop = async (d: StopListRow) => {
    setBusy(true);
    try {
      await post(`/dishes/${d.dishId}/${d.reason === 'Manual' ? 'resume' : 'stop'}`);
      await Promise.all([menu.reload(), offers.reload()]);
    } catch {
      // Связь оборвалась: состояние блюда не изменилось, кнопку можно нажать ещё раз.
    } finally {
      setBusy(false);
    }
  };

  // Продукт техкарты могли завести только что: строка появится, когда обновится справочник.
  const recipe = (dishes.data.find((d) => d.id === selected?.dishId)?.recipe ?? []).filter((r) => products.data!.some((p) => p.id === r.productId));

  return (
    <>
      <div className="g-title">
        <div className="menu-head">
          <h1>Меню и стоп-лист</h1>
          <p className="sub">Стоп-лист ведётся сам: порции считаются по годным партиям, а прогноз — по темпу продаж за смену.</p>
        </div>
        <div className="end">
          <div className="g-seg" role="group" aria-label="Статус блюда">
            {FILTERS.map((f) => (
              <button key={f.id} type="button" aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>
                {f.label} <span className="n num">{f.id === 'all' ? rows.length : rows.filter((d) => d.status === f.id).length}</span>
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="menu-row">
        <section className="menu-main" aria-label="Блюда">
          {shown.length === 0 ? (
            <p className="g-empty"><strong>Таких блюд нет</strong>Выберите другой статус.</p>
          ) : (
            <ul className="dish-grid">
              {shown.map((d) => {
                const reason = why(d);
                return (
                  <li key={d.dishId}>
                    <button className="dish-card" type="button" aria-pressed={selected?.dishId === d.dishId} onClick={() => setSelectedId(d.dishId)}>
                      <span className={`dish-card__photo${d.status === 'Stop' ? ' is-off' : ''}`}>
                        <Photo url={d.photoUrl} name={d.dishName} />
                        <span className={`${CHIPS[d.status][0]} ph-chip`}>{CHIPS[d.status][1]}</span>
                      </span>
                      <span className="dish-card__body">
                        <span className="dish-card__top"><span className="g-name">{d.dishName}</span><span className="num muted">{money(d.price)}</span></span>
                        <span className="dish-card__portions">
                          <span className="num" style={{ color: d.status === 'Stop' ? 'var(--red)' : d.status === 'Low' ? 'var(--amber)' : 'var(--h)' }}>{d.portions}</span>
                          <span>{plural(d.portions, 'порция', 'порции', 'порций').replace(/^\d+ /, '')}</span>
                        </span>
                        <span><span className="muted">Первым закончится:</span> <span className="hd">{d.limitingProductName.toLowerCase()}</span></span>
                        <span className="small dish-card__why" style={{ color: reason.color }}>{reason.text}</span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </section>

        {selected && (
          <aside className="menu-side" aria-labelledby="tc-h">
            <section className="g-panel">
              <div className={`menu-photo${selected.status === 'Stop' ? ' is-off' : ''}`}><Photo url={selected.photoUrl} name={selected.dishName} /></div>
              <div className="g-panel__head menu-side__head">
                <h2 id="tc-h">{selected.dishName}</h2>
                <span className={CHIPS[selected.status][0]}>{CHIPS[selected.status][1]}</span>
              </div>
              <div className="menu-side__body">
                <div className="facts-3">
                  <div className="fact"><div className="small">Цена</div><div className="v num">{money(selected.price)}</div></div>
                  <div className="fact"><div className="small">Себестоимость</div><div className="v num">{money(selected.cost)}</div></div>
                  <div className="fact"><div className="small">Хватит на</div><div className="v num">{selected.portions} порц.</div></div>
                </div>
                <p className="hd menu-forecast">{why(selected).text}</p>
                <h3 className="hd">Техкарта на 1 порцию</h3>
                <ul className="tk">
                  {recipe.map((r) => {
                    const product = products.data!.find((p) => p.id === r.productId)!;
                    const state = productState(product.id, stock.data!, batches.data!);
                    return (
                      <li key={r.productId} className={`tk--${state.kind}`}>
                        <span className="tk__bar" />
                        <span className="g-name">{product.name}</span>
                        <span className="num r hd tk__qty">{recipeQty(r.quantity, product.unit)}</span>
                        <span className="tk__wide muted small">{state.text}</span>
                      </li>
                    );
                  })}
                </ul>
                <div className="menu-actions">
                  {canCatalog && <Link className="g-btn" to={`/app/catalog/dishes?dish=${selected.dishId}`}>Изменить техкарту</Link>}
                  {canStop && <button className="g-btn menu-actions__stop" type="button" disabled={busy} onClick={() => toggleStop(selected)}>
                    {selected.reason === 'Manual' ? 'Вернуть в продажу' : 'Снять с продажи вручную'}
                  </button>}
                </div>
              </div>
            </section>
          </aside>
        )}
      </div>
    </>
  );
}

/** Доживёт ли запас до конца смены: прогноз стопа позже, чем время закрытия сегодня. */
function lastsUntilShiftEnd(forecast: string, shiftEnd?: string): boolean {
  if (!shiftEnd) return false;
  const [hours, minutes] = shiftEnd.split(':').map(Number);
  const end = new Date();
  end.setHours(hours, minutes, 0, 0);
  return new Date(forecast).getTime() > end.getTime();
}

/** Что с продуктом на складе — для строки техкарты. */
function productState(productId: string, stock: StockRow[], batches: BatchRow[]): { kind: string; text: string } {
  const row = stock.find((s) => s.productId === productId)!;
  const own = batches.filter((b) => b.productId === productId);
  const fresh = own.filter((b) => b.status !== 'Expired');
  if (fresh.length === 0) return own.length > 0 ? { kind: 'expired', text: 'годного нет · партия просрочена' } : { kind: 'stop', text: 'нет на складе' };
  const first = fresh[0];
  const total = qty(row.fresh, row.unit);
  if (first.hoursLeft < URGENT_HOURS) return { kind: 'hot', text: `${total} · срочно, до ${dueLong(first.expiresAt)}` };
  if (first.status === 'ExpiringSoon') return { kind: 'soon', text: `${total} · ближайшая партия до ${dueLong(first.expiresAt)}` };
  return { kind: 'ok', text: `${total} · до ${dueLong(first.expiresAt)}` };
}
