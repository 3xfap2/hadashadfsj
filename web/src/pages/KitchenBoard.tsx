import { useEffect, useState } from 'react';
import { post, useApi, type KitchenBoard as Board, type OrderRow } from '../api';
import { clock, plural } from '../format';

/** С какой минуты ожидания заказ помечается красным: гость ждёт слишком долго. */
const LATE_MINUTES = 20;

const wait = (minutes: number) =>
  minutes < 1 ? 'только что' : minutes < 60 ? `${minutes} мин` : `${Math.floor(minutes / 60)} ч ${minutes % 60} мин`;

const dishes = (order: OrderRow) => order.items.map((i) => (i.quantity > 1 ? `${i.quantity} × ` : '') + i.dishName.toLowerCase()).join(', ');

/** Экран кухни: очередь принятых заказов. Повар отмечает заказ готовым — он уходит из очереди, а официант видит отметку у стола. */
export function KitchenBoard() {
  const board = useApi<Board>('/orders/kitchen', 15_000);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState('');
  // Время ожидания растёт само, без запроса к серверу.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 15_000);
    return () => clearInterval(timer);
  }, []);

  if (board.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить очередь</h3><span>{board.error}</span></div>;
  if (!board.data) return <p className="muted">Загружаем очередь…</p>;

  const markReady = async (order: OrderRow) => {
    setBusy(order.id);
    setError('');
    try {
      const response = await post(`/orders/${order.id}/ready`);
      if (!response.ok) setError(`Заказ № ${order.number} не отмечен: сервер ответил ошибкой ${response.status}.`);
      await board.reload();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(null);
    }
  };

  const { queue, ready } = board.data;

  return (
    <>
      <div className="g-title">
        <h1>Кухня</h1>
        <span className="sub">{queue.length > 0 ? `В очереди ${plural(queue.length, 'заказ', 'заказа', 'заказов')} · сначала те, что ждут дольше` : 'Все заказы отданы'}</span>
      </div>

      {error && <div className="g-note g-note--err" role="alert"><span>{error}</span></div>}

      {queue.length === 0 ? (
        <section className="g-panel">
          <p className="g-empty"><strong>Очередь пуста</strong>Новый заказ появится здесь сразу, как только официант его оформит.</p>
        </section>
      ) : (
        <ul className="kb" aria-label="Очередь заказов">
          {queue.map((o) => {
            const minutes = Math.max(0, Math.floor((now - new Date(o.createdAt).getTime()) / 60_000));
            return (
              <li key={o.id} className="g-panel kb__card">
                <div className="kb__head">
                  <strong className="hd kb__place">{o.place || (o.delivery ? 'Доставка' : 'Без стола')}</strong>
                  <span className={minutes >= LATE_MINUTES ? 'g-tag g-tag--red num' : 'muted num'}>{wait(minutes)}</span>
                </div>
                <span className="muted small num">№ {o.number} · принят в {clock(o.createdAt)}{o.source && ` · ${o.source}`}</span>
                <ul className="kb__items">
                  {o.items.map((i) => (
                    <li key={i.dishId}><span className="num hd kb__qty">{i.quantity} ×</span><span className="g-name">{i.dishName}</span></li>
                  ))}
                </ul>
                <button className="g-btn g-btn--primary g-btn--block" type="button" disabled={busy === o.id} onClick={() => markReady(o)}>
                  {busy === o.id ? 'Отмечаем…' : 'Готов'}
                </button>
              </li>
            );
          })}
        </ul>
      )}

      {ready.length > 0 && (
        <section className="g-panel" aria-labelledby="kb-done-h">
          <div className="g-panel__head"><h2 id="kb-done-h">Только что отданы</h2></div>
          <ul className="g-rows kb-done">
            {ready.map((o) => (
              <li key={o.id}>
                <span className="num hd">№ {o.number}</span>
                <span>{o.source && `${o.source} · `}{o.place && `${o.place} · `}{dishes(o)}</span>
                <span className="num muted small">готов в {clock(o.readyAt!)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}
