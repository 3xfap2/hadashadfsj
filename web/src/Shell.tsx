import { Component, Suspense, lazy, useEffect, useState, type ReactNode } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { connectEvents, logout, takeFreshLogin, useApi, type ChatRow, type Me, type StopListRow } from './api';
import { Alerts } from './Alerts';
import { isNative } from './native';
import { clock } from './format';
import { AccessContext, useCan } from './ui';
import { KitchenBoard } from './pages/KitchenBoard';
import { Menu } from './pages/Menu';
import { Orders } from './pages/Orders';
import { Preps } from './pages/Preps';
import { Stock } from './pages/Stock';
import { Supplies } from './pages/Supplies';
import { Today } from './pages/Today';

// Экраны, которые открывают редко, подгружаются при первом заходе: вход и «Сегодня» открываются быстрее.
const Analytics = lazy(() => import('./pages/Analytics').then((m) => ({ default: m.Analytics })));
const Demo = lazy(() => import('./pages/Demo').then((m) => ({ default: m.Demo })));
const HotSettings = lazy(() => import('./pages/HotSettings').then((m) => ({ default: m.HotSettings })));
const Inventory = lazy(() => import('./pages/Inventory').then((m) => ({ default: m.Inventory })));
const Journal = lazy(() => import('./pages/Journal').then((m) => ({ default: m.Journal })));
const Labels = lazy(() => import('./pages/Labels').then((m) => ({ default: m.Labels })));
const Products = lazy(() => import('./pages/Catalog').then((m) => ({ default: m.Products })));
const Dishes = lazy(() => import('./pages/Catalog').then((m) => ({ default: m.Dishes })));
const ShiftReport = lazy(() => import('./pages/ShiftReport').then((m) => ({ default: m.ShiftReport })));
const Purchasing = lazy(() => import('./pages/Purchasing').then((m) => ({ default: m.Purchasing })));
const Staff = lazy(() => import('./pages/Staff').then((m) => ({ default: m.Staff })));
const SupplyImport = lazy(() => import('./pages/SupplyImport').then((m) => ({ default: m.SupplyImport })));

const SECTIONS = [
  { id: 'today', path: '/app', title: 'Сегодня' },
  { id: 'orders', path: '/app/orders', title: 'Заказы' },
  { id: 'board', path: '/app/kitchen-board', title: 'Кухня' },
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
  { id: 'board', path: '/app/kitchen-board', title: 'Кухня', icon: <path d="M4 11h16v3a6 6 0 0 1-6 6h-4a6 6 0 0 1-6-6zM2 11h20M9 7V4M12 7V3M15 7V4" /> },
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
  // Команда продукта работает в CRM: после входа открываем её. В кабинет демо-ресторана можно вернуться по ссылке из CRM.
  const vendor = me?.sections.includes('support') ?? false;
  useEffect(() => {
    if (me && takeFreshLogin() && vendor && !isNative) location.replace('/crm/');
  }, [me, vendor]);
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
        {me && <span className="shell__place muted small" title="Ресторан, в котором вы работаете">{me.restaurant}</span>}
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
          <Route index element={<Fence>{home}</Fence>} />
          <Route path="staff" element={<Guard section="staff"><Staff /></Guard>} />
          <Route path="orders" element={<Guard section="orders"><Orders /></Guard>} />
          <Route path="kitchen-board" element={<Guard section="board"><KitchenBoard /></Guard>} />
          <Route path="stock" element={<Guard section="stock"><Stock /></Guard>} />
          <Route path="stock/supplies" element={<Guard section="stock"><Supplies /></Guard>} />
          <Route path="stock/preps" element={<Guard section="kitchen"><Preps /></Guard>} />
          <Route path="stock/journal" element={<Guard section="stock"><Journal /></Guard>} />
          <Route path="stock/inventory" element={<Guard section="stock"><Inventory /></Guard>} />
          <Route path="labels" element={<Guard section="stock"><Labels /></Guard>} />
          <Route path="hot" element={<Guard section="catalog"><HotSettings /></Guard>} />
          <Route path="demo" element={<Guard section="catalog"><Demo /></Guard>} />
          <Route path="stock/import" element={<Guard section="stock"><SupplyImport /></Guard>} />
          <Route path="menu" element={<Guard section="menu"><Menu /></Guard>} />
          <Route path="purchasing" element={<Guard section="purchasing"><Purchasing /></Guard>} />
          <Route path="analytics" element={<Guard section="analytics"><Analytics /></Guard>} />
          <Route path="analytics/shift" element={<Guard section="analytics"><ShiftReport /></Guard>} />
          <Route path="catalog" element={<Guard section="catalog"><Products /></Guard>} />
          <Route path="catalog/dishes" element={<Guard section="catalog"><Dishes /></Guard>} />
          <Route path="more" element={<More sections={extra} />} />
          <Route path="support" element={<ToCrm />} />
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

/** Значок в шапке у команды продукта: ведёт в CRM (отдельное приложение на том же адресе) и показывает, сколько вопросов ждут ответа. */
function SupportLink() {
  const chats = useApi<ChatRow[]>('/support/chats');
  const waiting = chats.data?.reduce((sum, c) => sum + c.unread, 0) ?? 0;
  return (
    <a className="alerts__bell" href="/crm/" aria-label={waiting > 0 ? `Обращения, без ответа: ${waiting}` : 'Обращения'}>
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16v11H9l-5 4z" /></svg>
      {waiting > 0 && <span className="alerts__count num">{waiting > 99 ? '99+' : waiting}</span>}
    </a>
  );
}

/** Раздел, закрытый для роли, не открывается и по прямой ссылке. Сервер проверяет те же права сам. */
function Guard({ section, children }: { section: string; children: ReactNode }) {
  const { pathname } = useLocation();
  if (useCan(section)) return <Fence key={pathname}>{children}</Fence>;
  // Сюда попадают по старой ссылке или после входа под другой ролью — открываем главный экран этой роли.
  return <Navigate to="/app" replace />;
}

/** Ошибка на одном экране не должна оставить человека перед белой страницей без шапки и меню. */
export class Fence extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: unknown) {
    console.error('Экран не открылся', error);
  }

  render() {
    if (!this.state.failed) return <Suspense fallback={<p className="screen-wait muted" role="status">Открываем раздел…</p>}>{this.props.children}</Suspense>;
    return (
      <div className="g-note g-note--err" role="alert">
        <h3>Экран не открылся</h3>
        <span>Что-то пошло не так. Обновите страницу; если не поможет — напишите в поддержку.</span>
        <button className="g-btn" type="button" onClick={() => location.reload()}>Обновить страницу</button>
      </div>
    );
  }
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

/** Раздел «Обращения» переехал в CRM — отдельное приложение на том же адресе. */
function ToCrm() {
  useEffect(() => location.replace('/crm/'), []);
  return null;
}
