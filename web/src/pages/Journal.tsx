import { useState } from 'react';
import { useApi, type MovementRow } from '../api';
import { dueLong, qty } from '../format';
import { StockTabs } from './Stock';

type Filter = 'all' | 'Supply' | 'Order' | 'WriteOff' | 'Prep';

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'Все' },
  { id: 'Supply', label: 'Поставки' },
  { id: 'Order', label: 'Заказы' },
  { id: 'WriteOff', label: 'Списания' },
  { id: 'Prep', label: 'Заготовки' },
];
const TYPES = { Supply: 'Поставка', Order: 'Заказ', WriteOff: 'Списание', PrepUse: 'В заготовку', PrepMade: 'Заготовка' };

export function Journal() {
  const movements = useApi<MovementRow[]>('/movements?take=100');
  const [filter, setFilter] = useState<Filter>('all');

  if (movements.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить журнал</h3><span>{movements.error}</span></div>;
  if (!movements.data) return <p className="muted">Загружаем журнал…</p>;

  const shown = movements.data.filter((m) => filter === 'all' || m.type === filter || (filter === 'Prep' && m.type.startsWith('Prep')));

  return (
    <>
      <div className="g-title">
        <div className="stock-head">
          <h1>Склад</h1>
          <StockTabs />
        </div>
        <div className="end">
          <div className="g-seg" role="group" aria-label="Вид операции">
            {FILTERS.map((f) => (
              <button key={f.id} type="button" aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>
            ))}
          </div>
        </div>
      </div>

      <section className="g-panel" aria-labelledby="journal-h">
        <div className="g-panel__head">
          <h2 id="journal-h">Журнал операций</h2>
          <span className="end">последние {movements.data.length} · новые сверху</span>
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
                    <td className="num muted">{dueLong(m.at)}</td>
                    <td>{m.type === 'WriteOff' ? <span className="g-tag g-tag--amber">{TYPES[m.type]}</span> : TYPES[m.type]}</td>
                    <td className="g-name">{m.productName}</td>
                    <td className="r hd journal__qty">{m.type === 'Supply' || m.type === 'PrepMade' ? '+' : '−'}{qty(m.quantity, m.unit)}</td>
                    <td>{m.note ?? (m.type === 'Order' ? 'Продажа блюда' : '—')}</td>
                    <td className="muted">{m.actor || '—'}</td>
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
