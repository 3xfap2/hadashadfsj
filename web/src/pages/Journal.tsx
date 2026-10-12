import { useEffect, useState } from 'react';
import { useApi, type MovementRow } from '../api';
import { dueLong, qty } from '../format';
import { StockTabs } from './Stock';

type Filter = 'all' | 'Supply' | 'Order' | 'WriteOff' | 'Prep' | 'Inventory';

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Все' },
  { id: 'Supply', label: 'Поставки' },
  { id: 'Order', label: 'Заказы' },
  { id: 'WriteOff', label: 'Списания' },
  { id: 'Prep', label: 'Заготовки' },
  { id: 'Inventory', label: 'Инвентаризация' },
];
const TYPES = { Supply: 'Поставка', Order: 'Заказ', WriteOff: 'Списание', PrepUse: 'В заготовку', PrepMade: 'Заготовка', Shortage: 'Недостача', Surplus: 'Излишек', Opened: 'Вскрытие' };
// Сервер отдаёт не больше 500 последних операций.
const STEPS = [100, 300, 500];

export function Journal() {
  const [take, setTake] = useState(STEPS[0]);
  const movements = useApi<MovementRow[]>(`/movements?take=${take}`);
  const [filter, setFilter] = useState<Filter>('all');
  // Пока догружается следующая порция, на экране прежние строки.
  const [loadingMore, setLoadingMore] = useState(false);
  useEffect(() => setLoadingMore(false), [movements.data]);

  if (movements.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить журнал</h3><span>{movements.error}</span></div>;
  if (!movements.data) return <p className="muted">Загружаем журнал…</p>;

  const shown = movements.data.filter((m) =>
    filter === 'all' || m.type === filter || (filter === 'Prep' && m.type.startsWith('Prep')) || (filter === 'Inventory' && (m.type === 'Shortage' || m.type === 'Surplus')));
  const loaded = movements.data.length;
  // Пришло меньше, чем просили, — значит, это весь журнал.
  const whole = !loadingMore && loaded < take;
  const next = STEPS.find((s) => s > take);
  const scope = whole ? `Показан весь журнал, операций: ${loaded}` : `Показаны последние ${loaded} операций`;

  return (
    <>
      <div className="g-title">
        <div className="stock-head">
          <h1>Склад</h1>
          <StockTabs />
        </div>
        <div className="end">
          <div className="g-seg journal-filters" role="group" aria-label="Вид операции">
            {FILTERS.map((f) => (
              <button key={f.id} type="button" aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>
            ))}
          </div>
        </div>
      </div>

      <section className="g-panel" aria-labelledby="journal-h">
        <div className="g-panel__head">
          <h2 id="journal-h">Журнал операций</h2>
          <span className="end">{whole ? `все операции: ${loaded}` : `последние ${loaded}`} · новые сверху</span>
        </div>
        {shown.length === 0 ? (
          <p className="g-empty"><strong>Таких операций нет</strong>Выберите другой вид операции.</p>
        ) : (
          <div className="g-table-wrap">
            <table className="g-table journal">
              <thead>
                <tr><th>Время</th><th>Операция</th><th>Продукт</th><th className="r">Количество</th><th>Основание</th><th>Сотрудник</th></tr>
              </thead>
              <tbody>
                {shown.map((m) => (
                  <tr key={m.id}>
                    <td className="num muted journal__at">{dueLong(m.at)}</td>
                    <td className="journal__type">{m.type === 'WriteOff' || m.type === 'Shortage' ? <span className="g-tag g-tag--amber">{TYPES[m.type]}</span> : TYPES[m.type]}</td>
                    <td className="g-name journal__name">{m.productName}</td>
                    <td className="r hd journal__qty">{m.type === 'Supply' || m.type === 'PrepMade' || m.type === 'Surplus' ? '+' : '−'}{qty(m.quantity, m.unit)}</td>
                    <td className="journal__note">{m.note ?? (m.type === 'Order' ? (m.orderNumber ? `Заказ № ${m.orderNumber}` : 'Продажа блюда') : '—')}</td>
                    <td className="muted journal__actor">{m.actor || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="journal-more">
          <span className="muted small" role="status">
            {scope}{filter !== 'all' && ` · под фильтр подходят: ${shown.length}`}
            {!whole && !next && '. Более ранние операции здесь не показываются.'}
          </span>
          {!whole && next && (
            <button className="g-btn" type="button" disabled={loadingMore} onClick={() => { setLoadingMore(true); setTake(next); }}>
              {loadingMore ? 'Загружаем…' : 'Показать ещё'}
            </button>
          )}
        </div>
      </section>
    </>
  );
}
