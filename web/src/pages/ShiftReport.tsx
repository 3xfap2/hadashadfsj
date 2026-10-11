import { useState } from 'react';
import { useApi, type ShiftReport as Report } from '../api';
import { dueLong, money, plural, portions, qty } from '../format';
import { AnalyticsTabs, hours } from './Analytics';

const longDate = new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });

/**
 * Отчёт смены на одной странице: что продали, что потеряли, какие блюда стояли в стопе и что сделать к завтра.
 * Печатается как есть (Ctrl+P): шапка кабинета и кнопки в печать не попадают.
 */
export function ShiftReport() {
  // Пустая дата — текущая смена: сервер сам знает, какой сейчас день в ресторане.
  const [date, setDate] = useState('');
  const report = useApi<Report>(`/analytics/shift${date ? `?date=${date}` : ''}`, 60_000);

  if (report.error && !report.data) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить отчёт</h3><span>{report.error}</span></div>;
  if (!report.data) return <p className="muted">Собираем отчёт…</p>;

  const r = report.data;
  const accepted = r.orders - r.rejectedOrders;

  return (
    <div className="shift-report">
      <div className="an-head">
        <div className="buy-head">
          <h1>Аналитика</h1>
          <AnalyticsTabs />
          <p className="lead-text">Итог смены на одной странице: его можно распечатать или показать с телефона.</p>
        </div>
        <div className="shift-tools">
          <input className="g-input num" type="date" aria-label="Дата смены" value={date || r.date} onChange={(e) => setDate(e.target.value)} />
          {(date || !r.current) && <button className="g-btn" type="button" onClick={() => setDate('')}>Сегодня</button>}
          <button className="g-btn" type="button" onClick={() => window.print()}>Печать</button>
        </div>
      </div>

      {/* Отчёт за выбранную дату не загрузился: на экране остаётся прежний, дату можно выбрать заново. */}
      {report.error && <div className="g-note g-note--err" role="alert"><h3>Отчёт за эту дату не загрузился</h3><span>{report.error}</span></div>}

      <h2 className="shift-title hd">
        Отчёт смены · {longDate.format(new Date(`${r.date}T00:00`))}
        {r.current && <span className="muted"> · смена ещё идёт</span>}
      </h2>

      <dl className="g-kpis kpis-a" aria-label="Итоги смены">
        <div className="g-kpi g-kpi--main">
          <dt>Выручка</dt>
          <dd className="v num">{money(r.revenue)}</dd>
          <dd className="n num">{plural(accepted, 'заказ', 'заказа', 'заказов')}{accepted > 0 && ` · средний чек ${money(r.revenue / accepted)}`}</dd>
        </div>
        <div className={`g-kpi${r.rejectedOrders > 0 ? ' g-kpi--red' : ''}`}>
          <dt>Отказов в заказе</dt>
          <dd className="v num">{r.rejectedOrders}</dd>
          <dd className="n num">из {plural(r.orders, 'заказа', 'заказов', 'заказов')}</dd>
        </div>
        <div className="g-kpi">
          <dt>Потери</dt>
          <dd className="v num">{money(r.losses)}</dd>
          <dd className="n">списания и недостача{r.surplus > 0 && <span className="num"> · излишек +{money(r.surplus)}</span>}</dd>
        </div>
        <div className="g-kpi">
          <dt>Цена стопов</dt>
          <dd className="v num">{r.stopCost > 0 ? money(r.stopCost) : '—'}</dd>
          <dd className="n">{r.stops.length > 0 ? `${plural(r.stops.length, 'блюдо', 'блюда', 'блюд')} в стопе` : 'стопов не было'}</dd>
        </div>
      </dl>

      <div className="shift-grid">
        <section className="g-panel" aria-labelledby="sh-top">
          <div className="g-panel__head"><h2 id="sh-top">Продано</h2><span className="end">пять блюд с наибольшей выручкой</span></div>
          {r.topDishes.length === 0 ? (
            <p className="g-empty"><strong>Продаж не было</strong>За эту смену нет ни одного принятого заказа.</p>
          ) : (
            <table className="g-table">
              <thead><tr><th>Блюдо</th><th className="r">Порций</th><th className="r">Выручка</th></tr></thead>
              <tbody>
                {r.topDishes.map((d) => (
                  <tr key={d.dishId}><td className="g-name">{d.dishName}</td><td className="r num">{d.portions}</td><td className="r num hd">{money(d.revenue)}</td></tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <section className="g-panel" aria-labelledby="sh-loss">
          <div className="g-panel__head"><h2 id="sh-loss">Потери по причинам</h2><span className="end">по закупочным ценам</span></div>
          {r.writeOffs.length === 0 ? (
            <p className="g-empty"><strong>Потерь нет</strong>За смену ничего не списано, недостачи не найдено.</p>
          ) : (
            <table className="g-table">
              <thead><tr><th>Причина</th><th className="r">Сумма</th></tr></thead>
              <tbody>
                {r.writeOffs.map((w) => <tr key={w.reason}><td>{w.reason}</td><td className="r num hd">{money(w.value)}</td></tr>)}
                <tr><td className="g-name">Итого</td><td className="r num hd"><strong>{money(r.losses)}</strong></td></tr>
              </tbody>
            </table>
          )}
        </section>

        <section className="g-panel" aria-labelledby="sh-stop">
          <div className="g-panel__head"><h2 id="sh-stop">Блюда в стопе</h2><span className="end">в часы смены</span></div>
          {r.stops.length === 0 ? (
            <p className="g-empty"><strong>Стопов не было</strong>Все блюда продавались всю смену.</p>
          ) : (
            <table className="g-table">
              <thead><tr><th>Блюдо</th><th className="r">В стопе</th><th className="r">Недополучено</th></tr></thead>
              <tbody>
                {r.stops.map((s) => (
                  <tr key={s.dishId}>
                    <td className="g-name">{s.dishName}</td>
                    <td className="r num">{hours(s.stopHours)}</td>
                    <td className="r num hd">{s.lostRevenue > 0 ? `≈ ${money(s.lostRevenue)}` : '—'}{s.lostPortions > 0 && <div className="muted small">{portions(Math.round(s.lostPortions))}</div>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        {r.expiring && (
          <section className="g-panel" aria-labelledby="sh-exp">
            <div className="g-panel__head"><h2 id="sh-exp">Истекает до конца завтрашнего дня</h2></div>
            {r.expiring.length === 0 ? (
              <p className="g-empty"><strong>Сроки в порядке</strong>До конца завтрашнего дня ничего не истекает.</p>
            ) : (
              <table className="g-table">
                <thead><tr><th>Продукт</th><th className="r">Остаток</th><th className="r">Годен до</th></tr></thead>
                <tbody>
                  {r.expiring.map((e) => (
                    <tr key={e.productId}>
                      <td className="g-name">{e.productName}</td>
                      <td className="r num">{qty(e.quantity, e.unit)}<div className="muted small">{money(e.value)}</div></td>
                      <td className="r num">{dueLong(e.expiresAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        )}

        {r.toOrder && (
          <section className="g-panel" aria-labelledby="sh-buy">
            <div className="g-panel__head"><h2 id="sh-buy">Что заказать</h2>{r.toOrder.length > 0 && <span className="end num">на {money(r.toOrder.reduce((sum, p) => sum + p.sum, 0))}</span>}</div>
            {r.toOrder.length === 0 ? (
              <p className="g-empty"><strong>Заказывать нечего</strong>Запаса хватает по всем продуктам.</p>
            ) : (
              <table className="g-table">
                <thead><tr><th>Продукт</th><th className="r">Количество</th><th className="r">Сумма</th></tr></thead>
                <tbody>
                  {r.toOrder.map((p) => (
                    <tr key={p.productId}>
                      <td><span className="g-name">{p.productName}</span><div className="muted small">{p.supplier ?? 'поставщик не указан'}</div></td>
                      <td className="r num">{qty(p.quantity, p.unit)}</td>
                      <td className="r num hd">{money(p.sum)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        )}
      </div>
      {!r.current && <p className="muted small">Сроки годности и закупки показываются только в отчёте за текущую смену: это состояние склада на сейчас.</p>}
    </div>
  );
}
