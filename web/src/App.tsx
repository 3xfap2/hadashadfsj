import { useSyncExternalStore } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { getToken, onAuthChange } from './api';
import { Landing } from './pages/Landing';
import { Login } from './pages/Login';
import { Register } from './pages/Register';
import { Shell } from './Shell';

export function App() {
  const signedIn = useSyncExternalStore(onAuthChange, getToken) !== null;
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={signedIn ? <Navigate to="/app" replace /> : <Login />} />
        <Route path="/register" element={signedIn ? <Navigate to="/app" replace /> : <Register />} />
        <Route path="/app/*" element={signedIn ? <Shell /> : <Navigate to="/login" replace />} />
        <Route path="/" element={<Landing />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
}
