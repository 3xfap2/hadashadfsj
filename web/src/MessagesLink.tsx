import { NavLink } from 'react-router-dom';
import { useApi } from './api';

/** Значок в шапке: сообщения владельца ресторана и сколько из них ещё не прочитано. */
export function MessagesLink() {
  const unread = useApi<{ unread: number }>('/messages/unread').data?.unread ?? 0;
  return (
    <NavLink className="alerts__bell" to="/app/messages" aria-label={unread > 0 ? `Сообщения владельца, новых: ${unread}` : 'Сообщения владельца'}>
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16v11H9l-5 4z" /><path d="M8 9h8M8 12h5" /></svg>
      {unread > 0 && <span className="alerts__count num">{unread > 99 ? '99+' : unread}</span>}
    </NavLink>
  );
}
