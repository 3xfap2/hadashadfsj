import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { logout, useApi, type Me, type StopListRow } from './api';
import { clock } from './format';
import { Orders } from './pages/Orders';
import { Today } from './pages/Today';

const SECTIONS = [
  { path: '/app', title: 'Сегодня' },
  { path: '/app/orders', title: 'Заказы' },
  { path: '/app/stock', title: 'Склад' },
  { path: '/app/menu', title: 'Меню и стоп-лист' },
  { path: '/app/purchasing', title: 'Закупки' },
  { path: '/app/analytics', title: 'Аналитика' },
  { path: '/app/catalog', title: 'Справочники' },
];

// На телефоне внизу пять разделов; остальные собраны под «Ещё».
const TABS: { path: string; title: string; icon: ReactNode }[] = [
  { path: '/app', title: 'Сегодня', icon: <><circle cx="12" cy="13" r="8" /><path d="M12 9v4l3 2M9 2h6" /></> },
  { path: '/app/orders', title: 'Заказ', icon: <path d="M6 3h12v18l-3-2-3 2-3-2-3 2zM9 8h6M9 12h6" /> },
  { path: '/app/stock', title: 'Склад', icon: <path d="M3 8l9-5 9 5v8l-9 5-9-5zM3 8l9 5 9-5M12 13v8" /> },
  { path: '/app/menu', title: 'Меню', icon: <path d="M5 3v8a3 3 0 0 0 6 0V3M8 3v18M16 21V3c2.5 1 4 3.5 4 7h-4" /> },
  { path: '/app/more', title: 'Ещё', icon: <><circle cx="5" cy="12" r="1.5" /><circle cx="12" cy="12" r="1.5" /><circle cx="19" cy="12" r="1.5" /></> },
];

function useClock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 20_000);
    return () => clearInterval(timer);
  }, []);
  return clock(now.toISOString());
}

export function Shell() {
  const stopList = useApi<StopListRow[]>('/stop-list');
  const stopped = stopList.data?.filter((d) => d.status === 'Stop').length ?? 0;
  const time = useClock();
  const me = useApi<Me>('/me', 300_000).data;
  const title = me?.title || me?.login || '';
  // Инициалы: первые буквы слов должности («Шеф-повар» → «ШП»).
  const initials = title.split(/[\s-]+/).map((w) => w[0]).join('').slice(0, 2).toUpperCase();
  const count = stopped > 0 && <span className="cnt num" aria-label={`в стопе ${stopped}`}>{stopped}</span>;

  return (
    <div className="g shell">
      <header className="g-top shell__top">
        <NavLink className="g-brand" to="/app">Остаток</NavLink>
        <nav className="g-nav" aria-label="Разделы">
          {SECTIONS.map((s) => (
            <NavLink key={s.path} to={s.path} end={s.path === '/app'}>
              {s.title}
              {s.path === '/app/menu' && count}
            </NavLink>
          ))}
        </nav>
        <div className="g-end">
          <span className="muted">
            {me && `Смена ${me.shift.start}–${me.shift.end} · `}
            <b className="num hd">{time}</b>
          </span>
          <span className="shell__user">
            <span className="g-ava" aria-hidden="true">{initials}</span>
            <span className="hd">{title}</span>
          </span>
          <button className="shell__exit" onClick={logout}>Выйти</button>
        </div>
      </header>

      <header className="g-mtop shell__mtop">
        <span className="g-brand">Остаток</span>
        {me && <span className="muted">смена до {me.shift.end}</span>}
        <b className="num hd">{time}</b>
      </header>

      <main className="g-page">
        <Routes>
          <Route index element={<Today />} />
          <Route path="orders" element={<Orders />} />
          <Route path="more" element={<More />} />
          <Route path="*" element={<p className="g-empty"><strong>Раздел в работе</strong>Скоро появится.</p>} />
        </Routes>
      </main>

      <nav className="g-tabs shell__tabs" aria-label="Разделы">
        {TABS.map((t) => (
          <NavLink key={t.path} to={t.path} end={t.path === '/app'}>
            <svg viewBox="0 0 24 24" aria-hidden="true">{t.icon}</svg>
            {t.title}
            {t.path === '/app/menu' && count}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}

function More() {
  return (
    <>
      <div className="g-title"><h1>Ещё</h1></div>
      <section className="g-panel">
        <ul className="g-rows more">
          {SECTIONS.slice(4).map((s) => (
            <li key={s.path}><NavLink className="g-name" to={s.path}>{s.title}</NavLink></li>
          ))}
          <li><button className="g-btn" onClick={logout}>Выйти из кабинета</button></li>
        </ul>
      </section>
    </>
  );
}

export const toCabinet = <Navigate to="/app" replace />;
