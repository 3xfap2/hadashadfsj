import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { connectEvents, logout, useApi, type Me, type StopListRow } from './api';
import { Alerts } from './Alerts';
import { clock } from './format';
import { AccessContext, useCan } from './ui';
import { Analytics } from './pages/Analytics';
import { Demo } from './pages/Demo';
import { HotSettings } from './pages/HotSettings';
import { Journal } from './pages/Journal';
import { Labels } from './pages/Labels';
import { Dishes, Products } from './pages/Catalog';
import { Menu } from './pages/Menu';
import { Orders } from './pages/Orders';
import { Preps } from './pages/Preps';
import { Purchasing } from './pages/Purchasing';
import { Staff } from './pages/Staff';
import { Stock } from './pages/Stock';
import { Supplies } from './pages/Supplies';
import { Support, SupportLink } from './pages/Support';
import { SupplyImport } from './pages/SupplyImport';
import { Today } from './pages/Today';

const SECTIONS = [
  { id: 'today', path: '/app', title: 'Сегодня' },
  { id: 'orders', path: '/app/orders', title: 'Заказы' },
  { id: 'stock', path: '/app/stock', title: 'Склад' },
  { id: 'menu', path: '/app/menu', title: 'Меню и стоп-лист' },
  { id: 'purchasing', path: '/app/purchasing', title: 'Закупки' },
  { id: 'analytics', path: '/app/analytics', title: 'Аналитика' },
  { id: 'catalog', path: '/app/catalog', title: 'Справочники' },
  { id: 'staff', path: '/app/staff', title: 'Сотрудники' },
];
// Повару заготовки доступны отдельным пунктом: у него нет раздела «Склад», где они лежат вкладкой.
const PREPS = { id: 'kitchen', path: '/app/stock/preps', title: 'Заготовки' };

// На телефоне внизу пять разделов; остальные собраны под «Ещё».
const TABS: { id: string; path: string; title: string; icon: ReactNode }[] = [
  { id: 'today', path: '/app', title: 'Сегодня', icon: <><circle cx="12" cy="13" r="8" /><path d="M12 9v4l3 2M9 2h6" /></> },
  { id: 'orders', path: '/app/orders', title: 'Заказ', icon: <path d="M6 3h12v18l-3-2-3 2-3-2-3 2zM9 8h6M9 12h6" /> },
  { id: 'stock', path: '/app/stock', title: 'Склад', icon: <path d="M3 8l9-5 9 5v8l-9 5-9-5zM3 8l9 5 9-5M12 13v8" /> },
  { id: 'menu', path: '/app/menu', title: 'Меню', icon: <path d="M5 3v8a3 3 0 0 0 6 0V3M8 3v18M16 21V3c2.5 1 4 3.5 4 7h-4" /> },
  { id: 'more', path: '/app/more', title: 'Ещё', icon: <><circle cx="5" cy="12" r="1.5" /><circle cx="12" cy="12" r="1.5" /><circle cx="19" cy="12" r="1.5" /></> },
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
  useEffect(connectEvents, []);
  const profile = useApi<Me>('/me', 60_000);
  const me = profile.data;
  const title = me?.title || me?.login || '';
  // Инициалы: первые буквы слов должности («Шеф-повар» → «ШП»).
  const allowed = me?.sections ?? [];
  const sections = [
    ...SECTIONS.filter((s) => allowed.includes(s.id)),
    ...(allowed.includes('kitchen') && !allowed.includes('stock') ? [PREPS] : []),
  ];
  // Официант начинает с заказов: экрана смены у него нет.
  const home = allowed.length > 0 && !allowed.includes('today') ? <Navigate to="/app/orders" replace /> : <Today />;
  const tabs = TABS.filter((t) => t.id === 'more' || allowed.includes(t.id));
  // В «Ещё» попадают разделы, которым не хватило места в нижней панели.
  const extra = sections.filter((s) => !tabs.some((t) => t.id === s.id));
  const initials = title.split(/[\s-]+/).map((w) => w[0]).join('').slice(0, 2).toUpperCase();
  const count = stopped > 0 && <span className="cnt num" aria-label={`в стопе ${stopped}`}>{stopped}</span>;

  return (
    <AccessContext.Provider value={allowed}>
    <div className="g shell">
      <header className="g-top shell__top">
        <NavLink className="g-brand" to="/app" title={me?.restaurant}>Остаток</NavLink>
        <nav className="g-nav" aria-label="Разделы">
          {sections.map((s) => (
            <NavLink key={s.path} to={s.path} end={s.path === '/app'}>
              {s.title}
              {s.path === '/app/menu' && count}
            </NavLink>
          ))}
        </nav>
        <div className="g-end">
          {allowed.includes('support') && <SupportLink />}
          <Alerts />
          <span className="muted">
            {me && <span className="shell__shift">Смена {me.shift.start}–{me.shift.end} · </span>}
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
        {allowed.includes('support') && <SupportLink />}
        <Alerts />
      </header>

      <main className="g-page">
        {!me && (profile.error ? (
          <div className="g-note g-note--err" role="alert">
            <h3>Кабинет не загрузился</h3>
            <span>{profile.error}</span>
            <button className="g-btn" type="button" onClick={() => void profile.reload()}>Повторить</button>
          </div>
        ) : (
          <p className="muted" role="status">Загружаем кабинет… Если сервер давно не открывали, это занимает до минуты.</p>
        ))}
        {me && <Routes>
          <Route index element={home} />
          <Route path="staff" element={<Guard section="staff"><Staff /></Guard>} />
          <Route path="orders" element={<Guard section="orders"><Orders /></Guard>} />
          <Route path="stock" element={<Guard section="stock"><Stock /></Guard>} />
          <Route path="stock/supplies" element={<Guard section="stock"><Supplies /></Guard>} />
          <Route path="stock/preps" element={<Guard section="kitchen"><Preps /></Guard>} />
          <Route path="stock/journal" element={<Guard section="stock"><Journal /></Guard>} />
          <Route path="labels" element={<Guard section="stock"><Labels /></Guard>} />
          <Route path="support" element={<Guard section="support"><Support /></Guard>} />
          <Route path="hot" element={<Guard section="catalog"><HotSettings /></Guard>} />
          <Route path="demo" element={<Guard section="catalog"><Demo /></Guard>} />
          <Route path="stock/import" element={<Guard section="stock"><SupplyImport /></Guard>} />
          <Route path="menu" element={<Guard section="menu"><Menu /></Guard>} />
          <Route path="purchasing" element={<Guard section="purchasing"><Purchasing /></Guard>} />
          <Route path="analytics" element={<Guard section="analytics"><Analytics /></Guard>} />
          <Route path="catalog" element={<Guard section="catalog"><Products /></Guard>} />
          <Route path="catalog/dishes" element={<Guard section="catalog"><Dishes /></Guard>} />
          <Route path="more" element={<More sections={extra} />} />
          <Route path="*" element={<p className="g-empty"><strong>Раздел в работе</strong>Скоро появится.</p>} />
        </Routes>}
      </main>

      {allowed.includes('catalog') && (
        <footer className="shell__foot">
          <NavLink to="/app/demo">Генератор событий</NavLink>
        </footer>
      )}

      <nav className="g-tabs shell__tabs" aria-label="Разделы">
        {tabs.map((t) => (
          <NavLink key={t.path} to={t.path} end={t.path === '/app'}>
            <svg viewBox="0 0 24 24" aria-hidden="true">{t.icon}</svg>
            {t.title}
            {t.path === '/app/menu' && count}
          </NavLink>
        ))}
      </nav>
    </div>
    </AccessContext.Provider>
  );
}

/** Раздел, закрытый для роли, не открывается и по прямой ссылке. Сервер проверяет те же права сам. */
function Guard({ section, children }: { section: string; children: ReactNode }) {
  if (useCan(section)) return children;
  return <p className="g-empty"><strong>Раздел недоступен</strong>У вашей учётной записи нет доступа к этому разделу.</p>;
}

function More({ sections }: { sections: typeof SECTIONS }) {
  return (
    <>
      <div className="g-title"><h1>Ещё</h1></div>
      <section className="g-panel">
        <ul className="g-rows more">
          {sections.map((s) => (
            <li key={s.path}><NavLink className="g-name" to={s.path}>{s.title}</NavLink></li>
          ))}
          <li><button className="g-btn" onClick={logout}>Выйти из кабинета</button></li>
        </ul>
      </section>
    </>
  );
}

export const toCabinet = <Navigate to="/app" replace />;
