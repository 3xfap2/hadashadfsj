import { Capacitor } from '@capacitor/core';

/** true внутри приложения для Android; в браузере — false, и всё работает как раньше. */
export const isNative = Capacitor.isNativePlatform();

const SERVER = 'https://ostatok.onrender.com';

/** В браузере пусто: запросы идут на тот же адрес, что и сайт. В приложении сайт упакован внутрь, а API — на сервере. */
export const API_ORIGIN: string = import.meta.env.VITE_API_ORIGIN ?? (isNative ? SERVER : '');

/** Адрес для QR-кодов этикеток: в приложении location.origin — это внутренний адрес, с другого телефона он не откроется. */
export const PUBLIC_ORIGIN = API_ORIGIN || location.origin;

export const apiUrl = (path: string) => API_ORIGIN + path;

/** Загруженные фото лежат на сервере (/api/…), а фото демо-блюд (/dishes/…) упакованы вместе с сайтом. */
export const assetUrl = (url: string) => (url.startsWith('/api/') ? API_ORIGIN + url : url);
