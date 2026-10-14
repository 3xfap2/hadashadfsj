import { Component, useSyncExternalStore, type ReactNode } from 'react';
import { BrowserRouter, NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { getToken, logout, onAuthChange, useApi, type Dialog, type Me } from './api';
import { Wait } from './ui';
import { Login } from './pages/Login';
import { Messages } from './pages/Messages';
import { Staff } from './pages/Staff';
import { Summary } from './pages/Summary';
import { Suppliers } from './pages/Suppliers';

const SECTIONS: { path: string; title: string; icon: ReactNode }[] = [
  { path: '/', title: 'Сводка', icon: <path d="M4 20V10M10 20V4M16 20v-7M22 20H2" /> },
  { path: '/staff', title: 'Сотрудники', icon: <><circle cx="9" cy="8" r="3.5" /><path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6 6 0 0 1 3.5 6" /></> },
  { path: '/suppliers', title: 'Поставщики', icon: <path d="M2 6h11v10H2zM13 10h4l4 4v2h-8M6 19a2 2 0 1 0 0-.1M17 19a2 2 0 1 0 0-.1" /> },
  { path: '/messages', title: 'Сообщения', icon: <path d="M4 5h16v11H9l-5 4z" /> },
];

export function App() {
  const signedIn = useSyncExternalStore(onAuthChange, getToken) !== null;
  return (
    <BrowserRouter basename="/owner">
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

  if (!me) return <div className="g gate"><Wait error={profile.error} what="кабинет владельца" reload={profile.reload} /></div>;
  // Кабинет владельца — для того, кто добавил себе рестораны; сервер остальным тоже откажет.
  if (!me.sections.includes('owner')) {
    return (
      <div className="g gate">
        <div className="g-note g-note--err" role="alert">
          <h3>Нет доступа к кабинету владельца</h3>
          <span>
            Учётная запись «{me.login}» работает в кабинете ресторана. Кабинет владельца открыт только владельцу ресторанов —
            тому, кто добавил себе ресторан в разделе «Ресторан» → «Мои рестораны».
          </span>
          <span className="acts">
            <button className="g-btn g-btn--sm" type="button" onClick={logout}>Выйти</button>
            <a className="g-btn g-btn--sm" href="/app">В кабинет ресторана</a>
          </span>
        </div>
      </div>
    );
  }
  return <Workspace me={me} />;
}

function Workspace({ me }: { me: Me }) {
  const { pathname } = useLocation();
  // Новые ответы сотрудников видны в меню на любом экране; сообщения приходят по таймеру.
  const dialogs = useApi<Dialog[]>('/owner/messages', 15_000);
  const unread = dialogs.data?.reduce((sum, d) => sum + d.unread, 0) ?? 0;
  const count = (path: string) => (path === '/messages' && unread > 0 ? <span className="cnt num">{unread}</span> : null);

  return (
    <div className="g shell">
      <header className="g-top shell__top">
        <NavLink className="g-brand" to="/">Остаток <span className="shell__crm">владелец</span></NavLink>
        <nav className="g-nav" aria-label="Разделы">
          {SECTIONS.map((s) => <NavLink key={s.path} to={s.path} end={s.path === '/'}>{s.title}{count(s.path)}</NavLink>)}
        </nav>
        <div className="g-end">
          <a className="muted" href="/app"><span className="wide">Кабинет ресторана</span><span className="narrow">Ресторан</span></a>
          <span className="hd shell__user">{me.title || me.login}</span>
          <button className="shell__exit" type="button" onClick={logout}>Выйти</button>
        </div>
      </header>

      <main className="g-page">
        <Fence key={pathname.split('/')[1]}>
          <Routes>
            <Route index element={<Summary />} />
            <Route path="staff" element={<Staff me={me} />} />
            <Route path="suppliers" element={<Suppliers />} />
            <Route path="messages" element={<Messages />} />
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
