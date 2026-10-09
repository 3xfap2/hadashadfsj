import { useSyncExternalStore } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { getToken, onAuthChange } from './api';
import { Login } from './pages/Login';
import { Shell } from './Shell';

export function App() {
  const signedIn = useSyncExternalStore(onAuthChange, getToken) !== null;
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={signedIn ? <Navigate to="/app" replace /> : <Login />} />
        <Route path="/app/*" element={signedIn ? <Shell /> : <Navigate to="/login" replace />} />
        {/* Лендинг займёт главную; пока главная ведёт в кабинет. */}
        <Route path="*" element={<Navigate to="/app" replace />} />
      </Routes>
    </BrowserRouter>
  );
}
