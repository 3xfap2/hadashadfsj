import { Component, useEffect, useSyncExternalStore, type ReactNode } from 'react';
import { BrowserRouter, NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { connectEvents, getToken, logout, onAuthChange, useApi, type Me, type Summary } from './api';
import { Wait } from './ui';
import { Chats } from './pages/Chats';
import { Lead, Leads } from './pages/Leads';
import { Login } from './pages/Login';
import { Overview } from './pages/Overview';
import { Restaurant, Restaurants } from './pages/Restaurants';
import { TasksPage } from './pages/Tasks';

const SECTIONS: { path: string; title: string; icon: ReactNode }[] = [
  { path: '/', title: 'Обзор', icon: <path d="M4 20V10M10 20V4M16 20v-7M22 20H2" /> },
  { path: '/leads', title: 'Заявки', icon: <path d="M3 5h18l-7 8v6l-4-2v-4z" /> },
  { path: '/chats', title: 'Диалоги', icon: <path d="M4 5h16v11H9l-5 4z" /> },
  { path: '/restaurants', title: 'Рестораны', icon: <path d="M4 20V9l8-5 8 5v11M9 20v-6h6v6" /> },
  { path: '/tasks', title: 'Задачи', icon: <path d="M4 6l2 2 3-3M4 13l2 2 3-3M13 7h7M13 14h7M4 20h16" /> },
];

export function App() {
  const signedIn = useSyncExternalStore(onAuthChange, getToken) !== null;
  return (
    <BrowserRouter basename="/crm">
      <Routes>
        <Route path="/login" element={signedIn ? <Navigate to="/" replace /> : <Login />} />
        <Route path="/*" element={signedIn ? <Shell /> : <Navigate to="/login" replace />} />
      </Routes>
    </BrowserRouter>
  );
}

function Shell() {
  const profile = useApi<Me>('/me', 60_000);
  const me = profile.data;
  const allowed = me?.sections.includes('support') ?? false;

  if (!me) return <div className="g gate"><Wait error={profile.error} what="CRM" reload={profile.reload} /></div>;
  // CRM — инструмент команды продукта: сотруднику ресторана здесь делать нечего, сервер ему тоже откажет.
  if (!allowed) {
    return (
      <div className="g gate">
        <div className="g-note g-note--err" role="alert">
          <h3>Нет доступа к CRM</h3>
          <span>Учётная запись «{me.login}» работает в кабинете ресторана. CRM открыта только команде «Остатка».</span>
          <span className="acts">
            <button className="g-btn g-btn--sm" type="button" onClick={logout}>Выйти</button>
            <a className="g-btn g-btn--sm" href="/app">В кабинет</a>
          </span>
        </div>
      </div>
    );
  }
  return <Workspace me={me} />;
}

function Workspace({ me }: { me: Me }) {
  useEffect(connectEvents, []);
  const { pathname } = useLocation();
  const summary = useApi<Summary>('/crm/summary');
  const counts: Record<string, number | undefined> = {
    '/leads': summary.data?.leads.new,
    '/chats': summary.data?.unreadChats,
    '/tasks': summary.data?.tasksOverdue,
  };
  const count = (path: string) => (counts[path] ? <span className="cnt num">{counts[path]}</span> : null);

  return (
    <div className="g shell">
      <header className="g-top shell__top">
        <NavLink className="g-brand" to="/">Остаток <span className="shell__crm">CRM</span></NavLink>
        <nav className="g-nav" aria-label="Разделы">
          {SECTIONS.map((s) => <NavLink key={s.path} to={s.path} end={s.path === '/'}>{s.title}{count(s.path)}</NavLink>)}
        </nav>
        <div className="g-end">
          <a className="muted" href="/app">Кабинет</a>
          <span className="hd shell__user">{me.title || me.login}</span>
          <button className="shell__exit" type="button" onClick={logout}>Выйти</button>
        </div>
      </header>

      <main className="g-page">
        <Fence key={pathname.split('/')[1]}>
          <Routes>
            <Route index element={<Overview />} />
            <Route path="leads" element={<Leads />} />
            <Route path="leads/:id" element={<Lead />} />
            <Route path="chats" element={<Chats />} />
            <Route path="chats/:id" element={<Chats />} />
            <Route path="restaurants" element={<Restaurants />} />
            <Route path="restaurants/:id" element={<Restaurant />} />
            <Route path="tasks" element={<TasksPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Fence>
      </main>

      <nav className="g-tabs shell__tabs" aria-label="Разделы">
        {SECTIONS.map((s) => (
          <NavLink key={s.path} to={s.path} end={s.path === '/'}>
            <svg viewBox="0 0 24 24" aria-hidden="true">{s.icon}</svg>
            {s.title}
            {count(s.path)}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}

/** Ошибка на одном экране не должна оставить человека перед белой страницей без шапки и меню. */
class Fence extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: unknown) {
    console.error('Экран не открылся', error);
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return (
      <div className="g-note g-note--err" role="alert">
        <h3>Экран не открылся</h3>
        <span>Что-то пошло не так. Обновите страницу.</span>
        <button className="g-btn g-btn--sm" type="button" onClick={() => location.reload()}>Обновить страницу</button>
      </div>
    );
  }
}
