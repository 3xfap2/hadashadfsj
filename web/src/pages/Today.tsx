import { useState } from 'react';
import { Link } from 'react-router-dom';
import { post, useApi, type BatchRow, type HotOffer, type OrderRow, type OrderSummary, type ProductRow, type StopListRow, type SupplyRow } from '../api';
import { clock, dayStart, duration, money, plural, portions, qty, today, until } from '../format';
import { BatchLabel, Photo, PlusIcon, URGENT_HOURS, useCan } from '../ui';

const STOP_REASON = {
  Expired: 'просрочено',
  OutOfStock: 'нет на складе',
  NotEnough: 'не хватает на порцию',
  Manual: 'снято с продажи вручную',
};

export function Today() {
  const offers = useApi<HotOffer[]>('/hot-offers');
  const stopList = useApi<StopListRow[]>('/stop-list');
  const batches = useApi<BatchRow[]>('/stock/batches');
  const orders = useApi<OrderRow[]>('/orders?take=4');
  const day = useApi<OrderSummary>(`/orders/summary?since=${dayStart()}`);
  const products = useApi<ProductRow[]>('/products', 60_000);
  const supplies = useApi<SupplyRow[]>('/supplies', 60_000);
  const [writingOff, setWritingOff] = useState(false);
  const canOrder = useCan('orders');
  const canStock = useCan('stock');
  const canTune = useCan('catalog');
  const canStaff = useCan('staff');

  const error = offers.error ?? stopList.error ?? batches.error ?? orders.error ?? day.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить данные</h3><span>{error}</span></div>;
  if (!offers.data || !stopList.data || !batches.data || !orders.data || !day.data) return <p className="muted">Загружаем смену…</p>;

  const stopped = stopList.data.filter((d) => d.status === 'Stop');
  const low = stopList.data.filter((d) => d.status === 'Low');
  const expiring = batches.data.filter((b) => b.status === 'ExpiringSoon');
  const expired = batches.data.filter((b) => b.status === 'Expired');
  // Закупочные суммы повару сервер не отдаёт: вместо рублей он видит число партий.
  const costs = batches.data.every((b) => b.value !== null);
  const expiredValue = sum(expired.map((b) => b.value ?? 0));
  const { rejected } = day.data;

  const writeOffExpired = async () => {
    setWritingOff(true);
    try {
      await Promise.all(expired.map((b) => post(`/stock/batches/${b.id}/write-off`)));
      await Promise.all([batches.reload(), stopList.reload(), offers.reload()]);
    } catch {
      // Связь оборвалась: несписанные партии остались в списке, кнопку можно нажать ещё раз.
    } finally {
      setWritingOff(false);
    }
  };

  return (
    <>
      <div className="g-title">
        <h1>Сегодня</h1>
        <span className="sub">{today()}</span>
        <div className="end">
          {canStock && <Link className={`g-btn${canOrder ? ' title-secondary' : ''}`} to="/app/stock/supplies">Принять поставку</Link>}
          {canOrder && <Link className="g-btn g-btn--primary" to="/app/orders"><PlusIcon />Новый заказ</Link>}
        </div>
      </div>

      {products.data && supplies.data && (products.data.length === 0 || stopList.data.length === 0 || supplies.data.length === 0) && (
        <section className="g-panel start" aria-labelledby="start-h">
          <div className="g-panel__head"><h2 id="start-h">С чего начать</h2><span className="end">три шага до первого заказа</span></div>
          <ol className="start__steps">
            <li className={products.data.length > 0 ? 'done' : ''}>
              <strong>Заведите продукты</strong>
              <span>Название, единица, срок хранения и цена закупки. {canTune ? <Link to="/app/catalog">Справочник продуктов</Link> : 'Это делает управляющий или шеф-повар.'}</span>
            </li>
            <li className={stopList.data.length > 0 ? 'done' : ''}>
              <strong>Добавьте блюда и техкарты</strong>
              <span>Сколько какого продукта идёт в порцию — по техкарте система списывает продукты и ведёт стоп-лист. {canTune && <Link to="/app/catalog/dishes">Блюда и техкарты</Link>}</span>
            </li>
            <li className={supplies.data.length > 0 ? 'done' : ''}>
              <strong>Примите первую поставку</strong>
              <span>Каждая позиция станет партией со своим сроком годности. {canStock && <Link to="/app/stock/supplies">Принять поставку</Link>}</span>
            </li>
          </ol>
          {canStaff && <p className="muted start__note">Заказы оформляет официант: заведите ему учётную запись в разделе <Link to="/app/staff">«Сотрудники»</Link>.</p>}
        </section>
      )}

      <dl className="g-kpis">
        <div className={`g-kpi${stopped.length > 0 ? ' g-kpi--red' : ''}`}>
          <dt>В стопе</dt>
          <dd className="v num">{plural(stopped.length, 'блюдо', 'блюда', 'блюд')}</dd>
          <dd className="n">{stopped.map((d) => d.dishName.toLowerCase()).join(', ') || 'все блюда в продаже'}</dd>
        </div>
        <div className="g-kpi g-kpi--main">
          <dt>Может уйти в списание</dt>
          <dd className="v num">{costs ? money(sum(expiring.map((b) => b.value ?? 0))) : plural(expiring.length, 'партия', 'партии', 'партий')}</dd>
          <dd className="n num">
            {expiring.length > 0
              ? `${plural(expiring.length, 'партия', 'партии', 'партий')} ${until(expiring[expiring.length - 1].expiresAt)}`
              : 'за сутки ничего не истекает'}
          </dd>
        </div>
        <div className="g-kpi">
          <dt>Просрочено</dt>
          <dd className="v num">{costs ? money(expiredValue) : plural(expired.length, 'партия', 'партии', 'партий')}</dd>
          <dd className="n">{expired.map((b) => `${b.productName.toLowerCase()}, ${b.supplyNumber}`).join('; ') || 'просрочки нет'}</dd>
        </div>
        <div className="g-kpi">
          <dt>Заказов сегодня</dt>
          <dd className="v num">{day.data.orders}</dd>
          <dd className="n num">
            {rejected > 0 && `${plural(rejected, 'отказ', 'отказа', 'отказов')} · `}
            {money(day.data.revenue)}
          </dd>
        </div>
      </dl>

      <div className="today">
        <div className="today__col">
          <section className="g-panel" aria-labelledby="sell-h">
            <div className="g-panel__head">
              <h2 id="sell-h">Продать в первую очередь</h2>
              <span className="end">по сроку и сумме списания{canTune && <> · <Link to="/app/hot">настроить</Link></>}</span>
            </div>
            {offers.data.length === 0 ? (
              <p className="g-empty"><strong>Продвигать нечего</strong>В ближайшие двое суток ничего не истекает.</p>
            ) : (
              <>
                <div className="g-colhead sell" aria-hidden="true">
                  <span />
                  <span>Блюдо и причина</span>
                  <span className="r">Хватит на</span>
                  <span className="r">{offers.data.some((o) => o.valueAtRisk !== null) && 'В списание'}</span>
                </div>
                <ul className="g-rows">
                  {offers.data.map((o) => <Offer key={o.dishId} offer={o} />)}
                </ul>
              </>
            )}
          </section>

          <section className="g-panel" aria-labelledby="orders-h">
            <div className="g-panel__head">
              <h2 id="orders-h">Последние заказы</h2>
            </div>
            {orders.data.length === 0 ? (
              <p className="g-empty"><strong>Заказов пока нет</strong>Первый заказ смены появится здесь.</p>
            ) : (
              <ul className="g-rows">
                {orders.data.slice(0, 4).map((o) => (
                  <li key={o.id} className="ord">
                    <span className="num muted">{clock(o.createdAt)}</span>
                    <span className="num hd">№ {o.number}</span>
                    <span className="ord__items">
                      <span>
                        {o.place && `${o.place} · `}
                        {o.items.map((i) => (i.quantity > 1 ? `${i.quantity} × ` : '') + i.dishName.toLowerCase()).join(', ')}
                        {o.status === 'Rejected' && ` — не хватило: ${o.shortages.map((s) => `${s.productName.toLowerCase()} ${qty(s.required - s.available, s.unit)}`).join(', ')}`}
                      </span>
                      {o.status === 'Rejected' && <span className="g-tag g-tag--red">Отказ</span>}
                    </span>
                    <span className="num r hd ord__sum">{o.status === 'Accepted' ? money(o.total) : '—'}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>

        <div className="today__col">
          <section className="g-panel" aria-labelledby="stop-h">
            <div className="g-panel__head">
              <h2 id="stop-h">Стоп-лист</h2>
              {canOrder && <Link className="end" to="/app/menu">Меню</Link>}
            </div>
            {stopped.length + low.length === 0 ? (
              <p className="g-empty"><strong>Стоп-лист пуст</strong>Все блюда в продаже.</p>
            ) : (
              <ul className="g-rows">
                {stopped.map((d) => (
                  <li key={d.dishId} className="side">
                    <div className="side__text">
                      <span className="g-name">{d.dishName}</span>
                      <span className="muted small">{d.limitingProductName}: {STOP_REASON[d.reason!]}</span>
                    </div>
                    <span className="g-tag g-tag--red">Стоп</span>
                  </li>
                ))}
                {low.map((d) => (
                  <li key={d.dishId} className="side">
                    <div className="side__text">
                      <span className="g-name">{d.dishName}</span>
                      <span className="muted small">
                        {portions(d.portions)}
                        {d.stopForecastAt && ` · стоп примерно в ${clock(d.stopForecastAt)}`}
                      </span>
                    </div>
                    <span className="g-tag g-tag--amber">Заканчивается</span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="g-panel" aria-labelledby="batches-h">
            <div className="g-panel__head">
              <h2 id="batches-h">Партии со сроком</h2>
              {canStock && <Link className="end" to="/app/stock">Склад</Link>}
            </div>
            {expired.length + expiring.length === 0 ? (
              <p className="g-empty"><strong>Сроки в порядке</strong>В ближайшие сутки ни одна партия не истекает.</p>
            ) : (
              <ul className="g-rows">
                {[...expiring, ...expired].map((b) => (
                  <li key={b.id} className="batch">
                    <span className="g-name">{b.productName}</span>
                    <BatchLabel batch={b} />
                    <span className="muted small num batch__meta">{b.supplyNumber} · {qty(b.remaining, b.unit)}{b.value !== null && ` · ${money(b.value)}`}</span>
                  </li>
                ))}
              </ul>
            )}
            {expired.length > 0 && canStock && (
              <div className="g-panel__foot">
                <span className="muted">Просрочено: <b className="num hd">{money(expiredValue)}</b></span>
                <button className="g-btn" type="button" disabled={writingOff} onClick={writeOffExpired}>
                  {writingOff ? 'Списываем…' : 'Списать просроченное'}
                </button>
              </div>
            )}
          </section>
        </div>
      </div>
    </>
  );
}

function Offer({ offer }: { offer: HotOffer }) {
  const main = offer.ingredients[0];
  const urgent = main.hoursLeft < URGENT_HOURS;
  const tag = `${main.productName.toLowerCase()}, осталось ${duration(main.hoursLeft)}`;
  return (
    <li className="sell">
      <Photo url={offer.photoUrl} name={offer.dishName} />
      <div className="sell__text">
        <span className="g-name">{offer.dishName}</span>
        {urgent ? <span className="g-tag g-tag--red">{tag}</span> : <span className="g-soon">{tag}</span>}
        <span className="g-why">
          Истекает {qty(main.nearestQuantity, main.unit)}, продать {portions(offer.portions)}
          {main.alternativeDishes === 1 && ' · других блюд с этим продуктом нет'}
        </span>
        {offer.discount && (
          <span className="sell__discount" title={offer.discount.reason}>
            Можно дать скидку {offer.discount.percent}% — <span className="num">{money(offer.discount.price)}</span> вместо <span className="num">{money(offer.price)}</span>
          </span>
        )}
      </div>
      <div className="sell__nums">
        <span className="num r hd sell__portions">{portions(offer.availablePortions)}</span>
        {offer.valueAtRisk !== null && <span className="num r hd sell__loss">{money(offer.valueAtRisk)}</span>}
      </div>
    </li>
  );
}

const sum = (values: number[]) => values.reduce((a, b) => a + b, 0);
