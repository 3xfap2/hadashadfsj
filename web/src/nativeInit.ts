import { App } from '@capacitor/app';
import { reloadAll } from './api';
import { API_ORIGIN, isNative } from './native';

// Экраны, с которых «Назад» сворачивает приложение, а не возвращает на предыдущий экран.
const ROOTS = new Set(['/', '/login', '/app', '/app/orders']);

/** Поведение, которое есть только в приложении. В браузере ничего не делает. */
export function initNative() {
  if (!isNative) return;
  // Бесплатный сервер засыпает: будим его, пока человек вводит пароль.
  void fetch(`${API_ORIGIN}/health`).catch(() => {});

  // Со своим обработчиком системная кнопка «Назад» перестаёт листать историю сама, поэтому листаем здесь.
  App.addListener('backButton', ({ canGoBack }) => {
    const path = location.pathname.replace(/\/+$/, '') || '/';
    if (!canGoBack || ROOTS.has(path)) void App.minimizeApp();
    else history.back();
  }).catch((e) => console.error('Кнопка «Назад» не подключена', e));
  // Пока приложение свёрнуто, поток событий обрывается: при возврате данные перечитываются.
  App.addListener('appStateChange', ({ isActive }) => {
    if (isActive) reloadAll();
  }).catch((e) => console.error('Возврат в приложение не отслеживается', e));
}
