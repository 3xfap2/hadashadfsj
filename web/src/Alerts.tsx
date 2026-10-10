import { useEffect, useRef, useState } from 'react';
import { post, useApi, type AlertRow, type AlertsResponse } from './api';
import { dueLong, duration } from './format';

const DANGER = new Set<AlertRow['kind']>(['BatchExpired', 'DishStopped']);
const TOAST_MS = 7000;

/** Строка под заголовком сигнала; для партии срок пересчитывается на момент показа. */
function detail(alert: AlertRow): string {
  if (!alert.expiresAt) return alert.detail;
  const hours = (new Date(alert.expiresAt).getTime() - Date.now()) / 3_600_000;
  return `${alert.detail} · ${hours > 0 ? `осталось ${duration(hours)}` : `просрочено ${duration(hours)} назад`}`;
}

/**
 * Сигналы о сроках и стоп-листе: колокольчик со счётчиком в шапке и всплывающее сообщение,
 * когда сигнал приходит при открытом кабинете.
 */
export function Alerts() {
  const alerts = useApi<AlertsResponse>('/alerts');
  const [open, setOpen] = useState(false);
  const [toasts, setToasts] = useState<AlertRow[]>([]);
  // Самый свежий сигнал, который уже видели: всё новее него показывается всплывающим сообщением.
  const lastSeen = useRef<number | null>(null);
  const box = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const items = alerts.data?.items;
    if (!items) return;
    const newest = items[0]?.id ?? 0;
    if (lastSeen.current !== null && newest > lastSeen.current) {
      const fresh = items.filter((a) => a.id > lastSeen.current!).slice(0, 3);
      setToasts((t) => [...fresh, ...t].slice(0, 3));
      setTimeout(() => setToasts((t) => t.filter((a) => !fresh.includes(a))), TOAST_MS);
    }
    lastSeen.current = newest;
  }, [alerts.data]);

  // Список закрывается по клику мимо и по Escape.
  useEffect(() => {
    if (!open) return;
    const outside = (e: MouseEvent) => !box.current?.contains(e.target as Node) && setOpen(false);
    const escape = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    document.addEventListener('mousedown', outside);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('mousedown', outside);
      document.removeEventListener('keydown', escape);
    };
  }, [open]);

  const unread = alerts.data?.unread ?? 0;
  const toggle = () => {
    setOpen((o) => !o);
    // Открыли список — сигналы считаются прочитанными.
    if (!open && unread > 0) void post('/alerts/read').then(alerts.reload);
  };

  return (
    <div className="alerts" ref={box}>
      <button className="alerts__bell" type="button" aria-expanded={open} aria-label={unread > 0 ? `Сигналы, непрочитанных: ${unread}` : 'Сигналы'} onClick={toggle}>
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 17V11a6 6 0 0 1 12 0v6l2 2H4zM10 21h4" /></svg>
        {unread > 0 && <span className="alerts__count num">{unread > 99 ? '99+' : unread}</span>}
      </button>

      {open && (
        <div className="alerts__list g-panel" role="region" aria-label="Сигналы">
          <div className="g-panel__head"><h2>Сигналы</h2><span className="end">сроки годности и стоп-лист</span></div>
          {!alerts.data || alerts.data.items.length === 0 ? (
            <p className="g-empty"><strong>Сигналов нет</strong>Здесь появятся партии с истекающим сроком и блюда, ушедшие в стоп.</p>
          ) : (
            <ul>
              {alerts.data.items.map((a) => (
                <li key={a.id} className={`alert${DANGER.has(a.kind) ? ' alert--danger' : a.kind === 'DishBack' ? ' alert--ok' : ''}`}>
                  <span className="alert__bar" />
                  <span className="g-name">{a.title}</span>
                  <span className="muted small num alert__time">{dueLong(a.at)}</span>
                  <span className="muted small alert__detail">{detail(a)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      <div className="toasts" role="status" aria-live="polite">
        {toasts.map((a) => (
          <div key={a.id} className={`toast${DANGER.has(a.kind) ? ' toast--danger' : ''}`}>
            <strong>{a.title}</strong>
            <span>{detail(a)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
