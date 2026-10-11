import { Haptics, NotificationType } from '@capacitor/haptics';
import { isNative } from '../native';
import { useState } from 'react';
import { post, useApi, type HotOffer, type Me, type OrderRow, type StopListRow } from '../api';
import { clock, duration, isToday, money, portions, qty } from '../format';
import { Photo, URGENT_HOURS } from '../ui';

const STOP_REASON = {
  Expired: 'просрочено',
  OutOfStock: 'нет на складе',
  NotEnough: 'не хватает на порцию',
  Manual: 'снято с продажи вручную',
};

type Result = { kind: 'ok' | 'rejected'; order: OrderRow } | { kind: 'error'; message: string } | null;

/** В приложении телефон вибрирует: официант чувствует, принят заказ или отклонён, не глядя на экран. */
const buzz = (type: NotificationType) => {
  if (isNative) void Haptics.notification({ type }).catch(() => {});
};

export function Orders() {
  const menu = useApi<StopListRow[]>('/stop-list');
  const offers = useApi<HotOffer[]>('/hot-offers');
  const orders = useApi<OrderRow[]>('/orders?take=200');
  const places = useApi<Me>('/me', 300_000).data?.places ?? [];
  const [chosenPlace, setPlace] = useState('');
  const place = chosenPlace || places[0] || '';
  const [cart, setCart] = useState<Record<string, number>>({});
  // Блюда, которые кассир продаёт со скидкой горячего предложения.
  const [discounted, setDiscounted] = useState<Record<string, boolean>>({});
  const [search, setSearch] = useState('');
  const [result, setResult] = useState<Result>(null);
  const [busy, setBusy] = useState(false);

  const error = menu.error ?? offers.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить меню</h3><span>{error}</span></div>;
  if (!menu.data || !offers.data) return <p className="muted">Загружаем меню…</p>;

  // В меню блюда идут в постоянном порядке, чтобы строки не прыгали при изменении остатков.
  const dishes = [...menu.data].sort((a, b) => a.dishName.localeCompare(b.dishName, 'ru'));
  const shown = dishes.filter((d) => d.dishName.toLowerCase().includes(search.trim().toLowerCase()));
  const stopped = dishes.filter((d) => d.status === 'Stop').length;
  const inCart = dishes.filter((d) => cart[d.dishId]);
  const count = inCart.reduce((s, d) => s + cart[d.dishId], 0);
  // Скидка действует, пока блюдо остаётся в горячих предложениях; её размер считает сервер.
  const discountOf = (dishId: string) => (discounted[dishId] ? offers.data!.find((o) => o.dishId === dishId)?.discount ?? null : null);
  // Скидка действует только на порции, которые спасают истекающий продукт; остальные — по полной цене.
  const discountedPortions = (d: StopListRow) =>
    discountOf(d.dishId) ? Math.min(cart[d.dishId] ?? 0, offers.data!.find((o) => o.dishId === d.dishId)!.portions) : 0;
  const lineSum = (d: StopListRow) => {
    const cheaper = discountedPortions(d);
    return cheaper * (discountOf(d.dishId)?.price ?? d.price) + (cart[d.dishId] - cheaper) * d.price;
  };
  const total = inCart.reduce((s, d) => s + lineSum(d), 0);

  const change = (dishId: string, delta: number) =>
    setCart((c) => {
      const next = { ...c, [dishId]: Math.max(0, (c[dishId] ?? 0) + delta) };
      if (!next[dishId]) delete next[dishId];
      return next;
    });

  // Открытые заказы за каждым столом: сумма и время последнего. После «Закрыть стол» он снова свободен.
  const served = new Map<string, { total: number; last: string }>();
  for (const o of orders.data ?? []) {
    if (o.status !== 'Accepted' || !o.place || o.closedAt || !isToday(o.createdAt)) continue;
    const seen = served.get(o.place);
    served.set(o.place, { total: (seen?.total ?? 0) + o.total, last: seen && seen.last > o.createdAt ? seen.last : o.createdAt });
  }

  const submit = async () => {
    setBusy(true);
    try {
      const response = await post('/orders', {
        place,
        items: Object.entries(cart).map(([dishId, quantity]) => ({ dishId, quantity, discount: Boolean(discountOf(dishId)) })),
      });
      if (response.status === 201) {
        buzz(NotificationType.Success);
        setResult({ kind: 'ok', order: await response.json() });
        setCart({});
        setDiscounted({});
      } else if (response.status === 409) {
        buzz(NotificationType.Error);
        setResult({ kind: 'rejected', order: await response.json() });
      } else {
        setResult({ kind: 'error', message: `Сервер ответил ошибкой ${response.status}. Заказ не создан.` });
      }
      await Promise.all([menu.reload(), offers.reload(), orders.reload()]);
    } catch (e) {
      setResult({ kind: 'error', message: (e as Error).message });
    }
    setBusy(false);
  };

  const open = served.get(place);
  const closeTable = async () => {
    setBusy(true);
    try {
      const response = await post('/orders/close', { place });
      if (!response.ok) setResult({ kind: 'error', message: `Стол не закрыт: сервер ответил ошибкой ${response.status}.` });
      await orders.reload();
    } catch (e) {
      setResult({ kind: 'error', message: `Стол не закрыт. ${(e as Error).message}` });
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <div className="g-title">
        <h1>Новый заказ</h1>
        <span className="sub">Продукты спишутся из партий с самым ранним сроком</span>
      </div>

      {places.length > 0 && (
        <section className="g-panel hall" aria-labelledby="hall-h">
          <div className="g-panel__head">
            <h2 id="hall-h">Зал</h2>
            {open ? (
              <button className="g-btn g-btn--sm end" type="button" disabled={busy} onClick={closeTable}>
                Закрыть {place.toLowerCase()} · <span className="num">{money(open.total)}</span>
              </button>
            ) : (
              <span className="end">выберите стол · под номером — открытые заказы</span>
            )}
          </div>
          <div className="hall__grid" role="group" aria-label="Стол">
            {places.map((p) => {
              const table = /^Стол (\d+)$/.exec(p)?.[1];
              const seen = served.get(p);
              return (
                <button key={p} className="hall__table" type="button" aria-pressed={place === p} aria-label={p} onClick={() => setPlace(p)}>
                  <strong className={table ? 'num' : 'hall__word'}>{table ?? p}</strong>
                  {seen ? (
                    <><span className="num hd">{money(seen.total)}</span><span className="num muted small">{clock(seen.last)}</span></>
                  ) : (
                    <span className="muted small">свободен</span>
                  )}
                </button>
              );
            })}
          </div>
        </section>
      )}

      <div className="orders">
        <section className="g-panel" aria-labelledby="menu-h">
          <div className="g-panel__head orders__menu-head">
            <h2 id="menu-h">Меню</h2>
            <span className="muted num small">
              {dishes.length} блюд{stopped > 0 && ` · ${stopped} в стопе`}
            </span>
            <div className="search">
              <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="6" /><path d="m16 16 4 4" /></svg>
              <label className="sr" htmlFor="dish-search">Найти блюдо</label>
              <input className="g-input" id="dish-search" type="search" placeholder="Найти блюдо" value={search} onChange={(e) => setSearch(e.target.value)} />
            </div>
          </div>
          <div className="g-colhead dish-row" aria-hidden="true">
            <span />
            <span>Блюдо</span>
            <span className="r">Цена</span>
            <span className="r">Осталось</span>
            <span className="r">В заказ</span>
          </div>
          {shown.length === 0 ? (
            <p className="g-empty"><strong>Ничего не найдено</strong>Блюда с таким названием в меню нет.</p>
          ) : (
            <ul className="g-rows">
              {shown.map((d) => (
                <DishRow
                  key={d.dishId}
                  dish={d}
                  offer={offers.data!.find((o) => o.dishId === d.dishId)}
                  first={offers.data![0]?.dishId === d.dishId}
                  quantity={cart[d.dishId] ?? 0}
                  onChange={(delta) => change(d.dishId, delta)}
                  discounted={Boolean(discounted[d.dishId])}
                  onDiscount={() => setDiscounted((x) => ({ ...x, [d.dishId]: !x[d.dishId] }))}
                />
              ))}
            </ul>
          )}
        </section>

        <aside className="orders__side">
          <section className="g-panel orders__cart" aria-labelledby="cart-h">
            <div className="g-panel__head orders__cart-head"><h2 id="cart-h">Заказ</h2>{place && <span className="end hd">{place}</span>}</div>
            {inCart.length === 0 ? (
              <div className="g-empty"><strong>Заказ пуст</strong><span>Добавьте блюда кнопкой «+» в списке слева.</span></div>
            ) : (
              <ul>
                {inCart.map((d) => (
                  <li key={d.dishId} className="ord-row">
                    <span className="ord-row__text">
                      <span className="g-name">{d.dishName}</span>
                      <span className="muted num small">
                        {cart[d.dishId]} × {money(d.price)}
                        {discountOf(d.dishId) && ` · скидка ${discountOf(d.dishId)!.percent}% на ${portions(discountedPortions(d))}`}
                      </span>
                    </span>
                    <span className="num r hd ord-row__sum">{money(lineSum(d))}</span>
                    <button className="x" type="button" aria-label={`Удалить ${d.dishName}`} onClick={() => change(d.dishId, -cart[d.dishId])}>
                      <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18" /></svg>
                    </button>
                  </li>
                ))}
              </ul>
            )}
            <div className="orders__total">
              <div className="muted"><span>Позиций</span><span className="num">{count}</span></div>
              <div className="orders__sum"><span>Сумма</span><span className="num">{money(total)}</span></div>
              <button className="g-btn g-btn--primary g-btn--block" type="button" onClick={submit} disabled={count === 0 || busy}>
                {busy ? 'Оформляем…' : 'Оформить'}
              </button>
            </div>
          </section>

          <ResultNote result={result} />

          <div className="orders__bar">
            <div>
              <span className="muted num small">{count} поз.</span>
              <strong className="num hd">{money(total)}</strong>
            </div>
            <button className="g-btn g-btn--primary" type="button" onClick={submit} disabled={count === 0 || busy}>
              {busy ? 'Оформляем…' : 'Оформить'}
            </button>
          </div>
        </aside>
      </div>
    </>
  );
}

function DishRow(props: {
  dish: StopListRow; offer?: HotOffer; first: boolean; quantity: number; onChange: (delta: number) => void;
  discounted: boolean; onDiscount: () => void;
}) {
  const { dish, offer, first, quantity, onChange, discounted, onDiscount } = props;
  const stop = dish.status === 'Stop';
  const expiring = offer?.ingredients[0];

  let tag: { className: string; text: string; note?: string } | null = null;
  if (stop) {
    tag = { className: 'g-tag g-tag--red', text: 'Стоп', note: `${dish.limitingProductName.toLowerCase()}: ${STOP_REASON[dish.reason!]}` };
  } else if (expiring) {
    tag = {
      className: expiring.hoursLeft < URGENT_HOURS ? 'g-tag g-tag--red' : 'g-soon',
      text: `${expiring.productName.toLowerCase()}, осталось ${duration(expiring.hoursLeft)}`,
      note: first ? 'предложить первым' : undefined,
    };
  } else if (dish.status === 'Low') {
    tag = { className: 'g-tag g-tag--amber', text: 'Заканчивается', note: `${dish.limitingProductName.toLowerCase()} на ${portions(dish.portions)}` };
  }

  return (
    <li className={`dish-row${stop ? ' g-off' : ''}`}>
      <Photo url={dish.photoUrl} name={dish.dishName} />
      <div className="dish-row__text">
        <span className="g-name">{dish.dishName}</span>
        <span className="muted num small dish-row__mobile">{money(dish.price)} · {stop ? 'нет в продаже' : portions(dish.portions)}</span>
        {tag && (
          <span className="meta">
            <span className={tag.className}>{tag.text}</span>
            {tag.note && <span>{tag.note}</span>}
          </span>
        )}
        {!stop && offer?.discount && (
          <button className="dish-row__discount" type="button" aria-pressed={discounted} title={offer.discount.reason} onClick={onDiscount}>
            {discounted ? `Со скидкой ${offer.discount.percent}% — ${money(offer.discount.price)}` : `Продать со скидкой ${offer.discount.percent}% — ${money(offer.discount.price)}`}
          </button>
        )}
      </div>
      <span className="num r hd dish-row__wide">{money(dish.price)}</span>
      <span className="num r hd dish-row__wide dish-row__left">{stop ? '—' : portions(dish.portions)}</span>
      <span className="g-qty" role="group" aria-label={`Количество: ${dish.dishName}`}>
        <button type="button" aria-label="Убрать порцию" onClick={() => onChange(-1)} disabled={quantity === 0}>−</button>
        <span aria-live="polite">{quantity}</span>
        <button type="button" aria-label="Добавить порцию" onClick={() => onChange(1)} disabled={stop}>+</button>
      </span>
    </li>
  );
}

function ResultNote({ result }: { result: Result }) {
  if (!result) return null;
  if (result.kind === 'error') {
    return <section className="g-note g-note--err" role="alert"><h3>Заказ не создан</h3><span className="hd">{result.message}</span></section>;
  }
  const { order } = result;
  if (result.kind === 'rejected') {
    return (
      <section className="g-note g-note--err" role="alert">
        <h3>Заказ не создан</h3>
        <span className="hd">Не хватает продуктов. Ничего не списано.</span>
        <ul className="orders__lack">
          {order.shortages.map((s) => (
            <li key={s.productName}>
              <strong className="hd">{s.productName}:</strong> нужно <span className="num">{qty(s.required, s.unit)}</span>, есть{' '}
              <span className="num">{qty(s.available, s.unit)}</span>
            </li>
          ))}
        </ul>
      </section>
    );
  }
  return (
    <section className="g-note g-note--ok" aria-live="polite">
      <h3>Заказ № {order.number} принят · <span className="num">{clock(order.createdAt)}</span> · <span className="num">{money(order.total)}</span></h3>
      <span className="muted small">Списано из партий с ближайшим сроком:</span>
      <ul>
        {order.writeOffs.map((w, i) => (
          <li key={i} className="wo">
            <span className="hd">{w.productName}</span>
            <span className="muted num">{w.supplyNumber}</span>
            <span className="num r hd wo__qty">−{qty(w.quantity, w.unit)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
