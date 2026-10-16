import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useApi, type Me, type MovementRow } from '../api';
import { dueLong, plural, qty } from '../format';
import { StockTabs } from './Stock';

/** Проверка печати журнала (GET /stock/journal/verify): сервер пересчитывает хэш-цепочку с первой записи. */
interface JournalVerify {
  intact: boolean;
  sealed: number;
  unsealed: number;
  lastHash: string | null;
  lastSealedAt: string | null;
  brokenMovementId: number | null;
  brokenSealNo: number | null;
  message: string;
}

/**
 * Плашка печати журнала для управляющего: записи журнала запечатаны хэш-цепочкой, правка или удаление записи в базе
 * видны при проверке. Это защита от незаметной правки, а не от администратора базы, который пересчитает и хеши.
 */
function JournalSeal() {
  const verify = useApi<JournalVerify>('/stock/journal/verify', 60_000);
  const [checking, setChecking] = useState(false);
  const check = async () => {
    setChecking(true);
    try {
      await verify.reload();
    } finally {
      setChecking(false);
    }
  };
  const v = verify.data;
  if (!v && !verify.error) return null;

  return (
    <div className={`g-note ${v && !v.intact ? 'g-note--err' : 'g-note--ok'} journal-seal`} role={v && !v.intact ? 'alert' : 'status'}>
      <div className="journal-seal__text">
        <h3>
          {!v ? 'Печать журнала не проверена'
            : !v.intact ? (v.brokenSealNo ? `Журнал изменён в обход системы: запись № ${v.brokenSealNo}` : 'Остаток на складе не сходится с журналом')
            : v.sealed === 0 ? 'Журнал ещё не запечатан'
            : `Журнал запечатан: ${plural(v.sealed, 'запись', 'записи', 'записей')}, цепочка цела`}
        </h3>
        <span>
          {!v ? verify.error
            : !v.intact ? (v.brokenSealNo ? `${v.message} Записи до неё не тронуты.` : v.message)
            : `Каждая запись скреплена хешем с предыдущей, а остатки сверены с приходом и расходом: правка журнала или остатка прямо в базе видна при проверке.${v.unsealed > 0 ? ` Новых записей ждут печати: ${v.unsealed} — печать идёт раз в 30 секунд.` : ''}`}
        </span>
        {v?.lastHash && v.intact && <code className="journal-seal__hash" title="Последний хеш цепочки">{v.lastHash.slice(0, 16)}…</code>}
      </div>
      <button className="g-btn g-btn--sm" type="button" disabled={checking} onClick={check}>{checking ? 'Проверяем…' : 'Проверить'}</button>
    </div>
  );
}

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
  // ?batch=<id> — история одной партии: сюда ведёт ссылка из строки партии на складе.
  const batch = useSearchParams()[0].get('batch');
  const movements = useApi<MovementRow[]>(batch ? `/movements?take=500&batchId=${batch}` : `/movements?take=${take}`);
  const [filter, setFilter] = useState<Filter>('all');
  const me = useApi<Me>('/me', 300_000);
  // Пока догружается следующая порция, на экране прежние строки.
  const [loadingMore, setLoadingMore] = useState(false);
  useEffect(() => setLoadingMore(false), [movements.data]);

  if (movements.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить журнал</h3><span>{movements.error}</span></div>;
  if (!movements.data) return <p className="muted">Загружаем журнал…</p>;

  const shown = movements.data.filter((m) =>
    filter === 'all' || m.type === filter || (filter === 'Prep' && m.type.startsWith('Prep')) || (filter === 'Inventory' && (m.type === 'Shortage' || m.type === 'Surplus')));
  const loaded = movements.data.length;
  // Пришло меньше, чем просили, — значит, это весь журнал.
  const whole = !loadingMore && loaded < (batch ? 500 : take);
  const next = batch ? undefined : STEPS.find((s) => s > take);
  const scope = batch && whole ? `Операций с этой партией: ${loaded}` : whole ? `Показан весь журнал, операций: ${loaded}` : `Показаны последние ${loaded} операций`;

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

      {me.data?.role === 'Manager' && !batch && <JournalSeal />}

      <section className="g-panel" aria-labelledby="journal-h">
        <div className="g-panel__head">
          <h2 id="journal-h">{batch ? `История партии${movements.data[0] ? `: ${movements.data[0].productName}` : ''}` : 'Журнал операций'}</h2>
          <span className="end">
            {batch ? <Link to="/app/stock/journal">весь журнал</Link> : whole ? `все операции: ${loaded}` : `последние ${loaded}`} · новые сверху
          </span>
        </div>
        {shown.length === 0 ? (
          <p className="g-empty"><strong>Таких операций нет</strong>{batch && movements.data.length === 0 ? 'У этой партии нет записей в журнале.' : 'Выберите другой вид операции.'}</p>
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
                    {/* Вскрытие остаток не меняет: часть партии переходит во вскрытую, знака нет. */}
                    <td className="r hd journal__qty">{m.type === 'Supply' || m.type === 'PrepMade' || m.type === 'Surplus' ? '+' : m.type === 'Opened' ? '' : '−'}{qty(m.quantity, m.unit)}</td>
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
