import { useState } from 'react';
import { send, useApi, type OrderRow, type SimulatorSettings } from '../api';
import { clock, money } from '../format';
import { ExpiryCheck } from './ExpiryCheck';

/** Управление генератором событий: он имитирует кассу и поставщиков, чтобы показать систему в работе. */
export function Demo() {
  const settings = useApi<SimulatorSettings>('/simulator', 30_000);
  const orders = useApi<OrderRow[]>('/orders?take=8');
  const [rate, setRate] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  if (settings.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить генератор</h3><span>{settings.error}</span></div>;
  if (!settings.data) return <p className="muted">Загружаем генератор…</p>;

  const current = settings.data;
  const value = rate || String(current.ordersPerMinute);

  const apply = async (running: boolean) => {
    const ordersPerMinute = Number(value);
    if (!(ordersPerMinute >= 1 && ordersPerMinute <= 120)) return setError('Укажите от 1 до 120 заказов в минуту.');
    setBusy(true);
    setError('');
    const response = await send('PUT', '/simulator', { running, ordersPerMinute });
    if (!response.ok) setError(`Сервер ответил ошибкой ${response.status}.`);
    await settings.reload();
    setRate('');
    setBusy(false);
  };

  return (
    <>
      <div className="buy-head">
        <h1>Генератор событий</h1>
        <p className="lead-text">Имитирует кассу и поставщиков: создаёт заказы и довозит продукты, которые заканчиваются. Заказы проходят тем же путём, что и настоящие.</p>
      </div>

      <ExpiryCheck />

      <div className="sup-row">
        <section className="sup-main g-panel" aria-labelledby="gen-h">
          <div className="g-panel__head">
            <h2 id="gen-h">Поток заказов</h2>
            <span className="end">{current.running ? `идёт · ${current.ordersPerMinute} в минуту` : 'остановлен'}</span>
          </div>
          <div className="form-body">
            <div className={`g-field demo-rate${error ? ' g-field--error' : ''}`}>
              <label htmlFor="gen-rate">Заказов в минуту</label>
              <div className="g-unit">
                <input className="g-input num" id="gen-rate" inputMode="numeric" value={value} onChange={(e) => setRate(e.target.value)} />
                <span>в мин</span>
              </div>
              {error && <span className="g-error-text">{error}</span>}
              <span className="g-hint">Раз в минуту генератор довозит до четырёх продуктов, которых осталось меньше чем на 12 часов.</span>
            </div>
            <div className={`g-note ${current.running ? 'g-note--ok' : ''} demo-state`}>
              <h3>{current.running ? 'Генератор работает' : 'Генератор остановлен'}</h3>
              <span>{current.running ? 'Откройте «Сегодня» или «Меню» в соседней вкладке — цифры меняются без обновления страницы.' : 'Включите его, чтобы увидеть, как меняются остатки, стоп-лист и горячие предложения.'}</span>
            </div>
          </div>
          <div className="g-panel__foot cat-foot">
            {current.running ? <button className="g-btn" type="button" disabled={busy} onClick={() => apply(false)}>Остановить</button> : <span />}
            <button className="g-btn g-btn--primary" type="button" disabled={busy} onClick={() => apply(true)}>
              {current.running ? 'Применить скорость' : 'Запустить'}
            </button>
          </div>
        </section>

        <aside className="sup-side">
          <section className="g-panel" aria-labelledby="last-h">
            <div className="g-panel__head"><h2 id="last-h">Последние заказы</h2></div>
            {!orders.data || orders.data.length === 0 ? (
              <p className="g-empty"><strong>Заказов пока нет</strong>Они появятся здесь, как только генератор заработает.</p>
            ) : (
              <ul className="g-rows">
                {orders.data.map((o) => (
                  <li key={o.id} className="side">
                    <div className="side__text">
                      <span className="g-name">№ {o.number} · {clock(o.createdAt)}</span>
                      <span className="muted small">{o.items.map((i) => (i.quantity > 1 ? `${i.quantity} × ` : '') + i.dishName.toLowerCase()).join(', ')}</span>
                    </div>
                    {o.status === 'Rejected' ? <span className="g-tag g-tag--red">Отказ</span> : <span className="num hd">{money(o.total)}</span>}
                  </li>
                ))}
              </ul>
            )}
          </section>
        </aside>
      </div>
    </>
  );
}
