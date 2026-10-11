import { useEffect, useSyncExternalStore } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { getToken, onAuthChange } from './api';
import { isNative } from './native';
import { GuestMenu } from './pages/GuestMenu';
import { Landing } from './pages/Landing';
import { Login } from './pages/Login';
import { Shell } from './Shell';

const NEXT_KEY = 'ostatok.next';

/** Запоминает страницу кабинета, на которую шёл невошедший пользователь, и отправляет его на вход. */
function ToLogin() {
  const location = useLocation();
  try {
    // Запоминается только страница с параметрами (ссылка из QR-кода этикетки): обычный выход из кабинета
    // не должен вернуть следующего вошедшего на чужой экран.
    if (location.search) sessionStorage.setItem(NEXT_KEY, location.pathname + location.search);
  } catch {
    // Без хранилища после входа откроется главный экран кабинета.
  }
  return <Navigate to="/login" replace />;
}

/** После входа — на запомненную страницу, иначе на главный экран кабинета. */
function AfterLogin() {
  let next = '/app';
  try {
    next = sessionStorage.getItem(NEXT_KEY) ?? next;
  } catch {
    // см. выше
  }
  // Запись стирается после перехода, а не во время отрисовки: отрисовка может выполниться дважды.
  useEffect(() => {
    try {
      sessionStorage.removeItem(NEXT_KEY);
    } catch {
      // см. выше
    }
  }, []);
  return <Navigate to={next.startsWith('/app') ? next : '/app'} replace />;
}

export function App() {
  const signedIn = useSyncExternalStore(onAuthChange, getToken) !== null;
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={signedIn ? <AfterLogin /> : <Login />} />
        <Route path="/app/*" element={signedIn ? <Shell /> : <ToLogin />} />
        {/* Приложение открывается сразу на входе: лендинг нужен тем, кто ещё не клиент. */}
        <Route path="/" element={isNative ? <Navigate to="/login" replace /> : <Landing />} />
        <Route path="/menu/:restaurant" element={<GuestMenu />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
}
