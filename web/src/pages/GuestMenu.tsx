import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import type { GuestMenu as Menu } from '../api';
import { money } from '../format';
import { apiUrl } from '../native';
import { Photo } from '../ui';

type Dish = Menu['dishes'][number];

/** Меню для гостя: открывается без входа по QR-коду со стола. Блюд из стоп-листа в нём нет, предложения дня — сверху. */
export function GuestMenu() {
  const { restaurant } = useParams();
  const [menu, setMenu] = useState<Menu | null>(null);
  const [failed, setFailed] = useState<'none' | 'missing' | 'offline'>('none');

  useEffect(() => {
    const load = () =>
      fetch(apiUrl(`/api/public/menu/${restaurant}`))
        .then(async (r) => {
          if (r.ok) {
            setMenu(await r.json());
            setFailed('none');
          } else setFailed(r.status === 404 || r.status === 400 ? 'missing' : 'offline');
        })
        .catch(() => setFailed('offline'));
    void load();
    // Стоп-лист меняется в течение вечера: меню у гостя обновляется само.
    const timer = setInterval(load, 60_000);
    return () => clearInterval(timer);
  }, [restaurant]);

  if (failed === 'missing') return <main className="g guest"><p className="g-empty"><strong>Меню не найдено</strong>Проверьте ссылку или попросите меню у официанта.</p></main>;
  if (!menu) return <main className="g guest"><p className="muted">{failed === 'offline' ? 'Нет связи. Пробуем ещё раз…' : 'Загружаем меню…'}</p></main>;

  const offers = menu.dishes.filter((d) => d.offerPrice !== null);
  const groups = new Map<string, Dish[]>();
  for (const d of menu.dishes) groups.set(d.category ?? 'Меню', [...(groups.get(d.category ?? 'Меню') ?? []), d]);

  return (
    <main className="g guest">
      <header className="guest__head">
        <h1>{menu.restaurant}</h1>
        <span className="muted">Меню на сейчас · всё, что есть в списке, можно заказать</span>
      </header>

      {offers.length > 0 && (
        <section className="guest__offers" aria-labelledby="offers-h">
          <h2 id="offers-h">Предложения дня</h2>
          <ul>{offers.map((d) => <Row key={d.dishId} dish={d} />)}</ul>
        </section>
      )}

      {menu.dishes.length === 0 && <p className="g-empty"><strong>Сейчас кухня не принимает заказы</strong>Загляните чуть позже.</p>}
      {[...groups].map(([name, dishes]) => (
        <section key={name} aria-label={name}>
          <h2>{name}</h2>
          <ul>{dishes.map((d) => <Row key={d.dishId} dish={d} />)}</ul>
        </section>
      ))}
      <footer className="muted small">Чтобы заказать, позовите официанта. Скидку по предложению дня он применит сам.</footer>
    </main>
  );
}

function Row({ dish }: { dish: Dish }) {
  return (
    <li className="guest__dish">
      <Photo url={dish.photoUrl} name={dish.name} />
      <span className="g-name">{dish.name}</span>
      {dish.offerPrice !== null ? (
        <span className="guest__price">
          <s className="muted num">{money(dish.price)}</s>
          <strong className="num hd">{money(dish.offerPrice)}</strong>
          <span className="g-tag g-tag--amber num">−{dish.offerPercent}%</span>
        </span>
      ) : (
        <strong className="num hd guest__price">{money(dish.price)}</strong>
      )}
    </li>
  );
}
