import { useState } from 'react';
import { useApi, type AnalyticsSummary, type DeficitRow, type StopCostReport, type WriteOffReport } from '../api';
import { dayMonthShort, money, plural, portions, qty, weekdayLong } from '../format';

const PERIODS = [7, 14, 30];
const AXIS_STEPS = 4;

export function Analytics() {
  const [days, setDays] = useState(14);
  const report = useApi<WriteOffReport>(`/analytics/write-offs?days=${days}`, 60_000);
  const deficit = useApi<DeficitRow[]>(`/analytics/deficit?days=${days}`, 60_000);
  const summary = useApi<AnalyticsSummary>(`/analytics/summary?days=${days}`, 60_000);
  const stopCost = useApi<StopCostReport>(`/analytics/stop-cost?days=${days}`, 60_000);

  const error = report.error ?? deficit.error ?? summary.error ?? stopCost.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить аналитику</h3><span>{error}</span></div>;
  // При смене периода старые данные остаются на экране, пока грузятся новые, — без мигания.
  if (!report.data || !deficit.data || !summary.data || !stopCost.data) return <p className="muted">Считаем аналитику…</p>;

  const byDay = report.data.byDay;
  const peak = byDay.reduce((max, d) => (d.value > max.value ? d : max), byDay[0]);
  const top = niceTop(peak.value);
  const clean = byDay.filter((d) => d.value === 0).length;
  const labelEvery = Math.ceil(byDay.length / 14);
  const reasons = groupReasons(report.data.byReason);
  const maxProduct = report.data.byProduct[0]?.value ?? 0;

  return (
    <>
      <div className="an-head">
        <div className="buy-head">
          <h1>Аналитика</h1>
          <p className="lead-text">Сколько денег ушло в списание и каких продуктов не хватает чаще всего.</p>
        </div>
        <div className="g-seg" role="group" aria-label="Период">
          {PERIODS.map((p) => (
            <button key={p} type="button" aria-pressed={days === p} onClick={() => setDays(p)}>{plural(p, 'день', 'дня', 'дней')}</button>
          ))}
        </div>
      </div>

      <dl className="g-kpis kpis-a" aria-label="Итоги периода">
        <div className="g-kpi g-kpi--main">
          <dt>Списано за {plural(days, 'день', 'дня', 'дней')}</dt>
          <dd className="v num">{money(report.data.total)}</dd>
          <dd className="n num">{dayMonthShort(byDay[0].date)} – {dayMonthShort(byDay[byDay.length - 1].date)}</dd>
        </div>
        <div className="g-kpi">
          <dt>В среднем в день</dt>
          <dd className="v num">{money(report.data.total / days)}</dd>
          <dd className="n">{plural(clean, 'день', 'дня', 'дней')} без списаний</dd>
        </div>
        <div className="g-kpi">
          <dt>Блюда в стопе</dt>
          <dd className="v num">{Math.round(summary.data.stopHours)} ч</dd>
          <dd className="n num">{stopCost.data.total > 0 ? `недополучено около ${money(stopCost.data.total)}` : 'суммарно по всем блюдам'}</dd>
        </div>
        <div className="g-kpi">
          <dt>Отказов в заказе</dt>
          <dd className="v num">{summary.data.rejectedOrders}</dd>
          <dd className="n num">из {plural(summary.data.orders, 'заказа', 'заказов', 'заказов')}</dd>
        </div>
      </dl>

      <section className="g-panel" aria-labelledby="days-h">
        <div className="g-panel__head an-wrap">
          <h2 id="days-h">Списания по дням, ₽</h2>
          {peak.value > 0 && <span className="end">максимум — {money(peak.value)} в {accusativeDay(peak.date)} {dayMonthShort(peak.date)}</span>}
        </div>
        <div className="pbody">
          {report.data.total === 0 ? (
            <p className="g-empty"><strong>Списаний не было</strong>За выбранный период ничего не списано.</p>
          ) : (
            <div className="bars" role="img" aria-label={`Столбики списаний по дням с ${dayMonthShort(byDay[0].date)} по ${dayMonthShort(byDay[byDay.length - 1].date)}`}>
              <div className="axis" aria-hidden="true">
                {Array.from({ length: AXIS_STEPS + 1 }, (_, i) => <span key={i}>{Math.round((top * (AXIS_STEPS - i)) / AXIS_STEPS).toLocaleString('ru-RU')}</span>)}
              </div>
              <div className="plot" aria-hidden="true">
                {byDay.map((d, i) => (
                  <div key={d.date} className={`bar${i === byDay.length - 1 ? ' today' : ''}`} style={{ height: Math.round(Math.max((d.value / top) * 220, d.value ? 4 : 0)) }} title={`${dayMonthShort(d.date)}: ${money(d.value)}`} />
                ))}
              </div>
              <div className="days" aria-hidden="true">
                {byDay.map((d, i) => <span key={d.date}>{i % labelEvery === 0 ? dayMonthShort(d.date) : ''}</span>)}
              </div>
            </div>
          )}
        </div>
      </section>

      <div className="sup-row">
        <section className="sup-main g-panel an-products" aria-labelledby="prod-h">
          <div className="g-panel__head an-wrap"><h2 id="prod-h">По продуктам</h2><span className="end">рубли и доля от всех списаний</span></div>
          <div className="pbody pbody--tight">
            {report.data.byProduct.length === 0 && <p className="muted">За период ничего не списано.</p>}
            {report.data.byProduct.map((p) => (
              <div className="hbar" key={p.productId}>
                <span className="hd hbar__name">{p.productName} <span className="muted">· {qty(p.quantity, p.unit)}</span></span>
                <span className="num r"><strong className="hd">{money(p.value)}</strong> <span className="muted">{Math.round((p.value / report.data!.total) * 100)}%</span></span>
                <div className="hbar__track"><div className="hbar__fill" style={{ width: `${(p.value / maxProduct) * 100}%` }} /></div>
              </div>
            ))}
            {reasons.length > 0 && (
              <div className="an-reasons">
                {reasons.map((r) => <span key={r.name}><span className="muted">{r.name}:</span> <strong className="num hd">{money(r.value)}</strong></span>)}
              </div>
            )}
          </div>
        </section>

        <section className="sup-side g-dark board" aria-labelledby="def-h">
          <h2 id="def-h">Чаще всего не хватает</h2>
          <span className="muted">за {plural(days, 'день', 'дня', 'дней')} · по часам в стопе</span>
          {deficit.data.length === 0 ? (
            <p className="board__empty">За период ни одно блюдо не стояло в стопе и ни один заказ не отклонён.</p>
          ) : (
            <ol>
              {deficit.data.slice(0, 5).map((x, i) => (
                <li className="board__item" key={x.productId}>
                  <span className="board__rank num">{i + 1}</span>
                  <div className="board__text">
                    <span className="g-name">{x.productName}</span>
                    {x.dishes.length > 0 && <span>{x.dishes.join(', ')}</span>}
                    <span className="muted small">
                      {x.rejectedOrders > 0
                        ? `${plural(x.rejectedOrders, 'отказ', 'отказа', 'отказов')} в заказе · не хватило ${qty(x.missingQuantity, x.unit)}`
                        : 'отказов не было'}
                    </span>
                  </div>
                  <div className="board__end"><span className="board__loss num">{hours(x.stopHours)}</span><span className="muted small">в стопе</span></div>
                </li>
              ))}
            </ol>
          )}
        </section>
      </div>

      <section className="g-panel" aria-labelledby="cost-h">
        <div className="g-panel__head an-wrap">
          <h2 id="cost-h">Цена стопа</h2>
          <span className="end">сколько выручки блюдо не принесло, пока стояло в стопе в часы смены</span>
        </div>
        {stopCost.data.dishes.length === 0 ? (
          <p className="g-empty"><strong>Стопов не было</strong>За выбранный период ни одно блюдо не стояло в стопе в часы смены.</p>
        ) : (
          <div className="g-table-wrap">
            <table className="g-table">
              <thead>
                <tr><th>Блюдо</th><th className="r">В стопе</th><th className="r">Обычно продаёт</th><th className="r">Не продано</th><th className="r">Недополучено</th></tr>
              </thead>
              <tbody>
                {stopCost.data.dishes.map((d) => (
                  <tr key={d.dishId}>
                    <td className="g-name">{d.dishName}</td>
                    <td className="r num">{hours(d.stopHours)}</td>
                    <td className="r num">{d.portionsPerHour > 0 ? `${d.portionsPerHour.toLocaleString('ru-RU')} в час` : 'нет продаж'}</td>
                    <td className="r num">{d.lostPortions > 0 ? `≈ ${portions(Math.round(d.lostPortions))}` : '—'}</td>
                    <td className="r num hd"><strong>{d.lostRevenue > 0 ? money(d.lostRevenue) : '—'}</strong></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  );
}

const hours = (value: number) => (value < 1 ? `${Math.round(value * 60)} мин` : `${Math.round(value)} ч`);

/** Верх шкалы: ближайшее «круглое» число выше максимума, чтобы подписи оси были ровными. */
function niceTop(max: number): number {
  if (max <= 0) return 1000;
  const step = 10 ** Math.floor(Math.log10(max)) / 4;
  return Math.ceil(max / (step * AXIS_STEPS)) * step * AXIS_STEPS;
}

/** «в пятницу», «в среду» — день недели в винительном падеже. */
function accusativeDay(date: string): string {
  const day = weekdayLong(date);
  return day.endsWith('а') ? `${day.slice(0, -1)}у` : day;
}

/** Причины из журнала сводятся к трём группам макета; автосписание — тоже «истёк срок». */
function groupReasons(rows: { reason: string; value: number }[]): { name: string; value: number }[] {
  const groups = new Map<string, number>();
  for (const row of rows) {
    const name = row.reason.startsWith('Истёк срок') ? 'Истёк срок' : row.reason;
    groups.set(name, (groups.get(name) ?? 0) + row.value);
  }
  return [...groups].map(([name, value]) => ({ name, value })).sort((a, b) => b.value - a.value);
}
