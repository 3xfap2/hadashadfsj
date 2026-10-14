import { useEffect, useRef, useState, type FormEvent } from 'react';
import { post, problemText, useApi } from '../api';
import { dueLong } from '../format';

interface StaffMessage {
  id: number;
  at: string;
  fromOwner: boolean;
  author: string;
  /** Сообщение владельца всем сотрудникам ресторана. */
  toAll: boolean;
  text: string;
}

/** Переписка сотрудника с владельцем ресторана: свои сообщения и сообщения владельца всем сотрудникам. */
export function OwnerMessages() {
  const list = useApi<{ unread: number; messages: StaffMessage[] }>('/messages', 15_000);
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const end = useRef<HTMLLIElement>(null);
  const unread = list.data?.unread ?? 0;
  const count = list.data?.messages.length ?? 0;

  // Открытый экран — всё прочитано, в том числе то, что пришло, пока он открыт.
  useEffect(() => {
    if (unread > 0) void post('/messages/read').catch(() => {});
  }, [unread]);
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'nearest' });
  }, [count]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;
    setBusy(true);
    setError('');
    try {
      const response = await post('/messages', { text });
      if (!response.ok) return setError(await problemText(response));
      setText('');
      await list.reload();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <div className="g-title"><h1>Сообщения</h1><span className="sub">переписка с владельцем ресторана</span></div>
      <section className="g-panel" aria-label="Переписка с владельцем">
        <div className="form-body">
          {list.error && !list.data && <div className="g-note g-note--err" role="alert"><h3>Сообщения не загрузились</h3><span>{list.error}</span></div>}
          {!list.data && !list.error && <p className="muted" role="status">Загружаем сообщения…</p>}
          {list.data?.messages.length === 0 && <p className="muted">Сообщений пока нет. Здесь появится то, что напишет владелец, — ему можно написать и самому.</p>}
          {list.data && list.data.messages.length > 0 && (
            <ul className="msgs" style={{ alignSelf: 'stretch' }}>
              {list.data.messages.map((m) => (
                <li key={m.id} className={`msg${m.fromOwner ? '' : ' msg--mine'}`}>
                  {m.fromOwner && <b className="small">{m.toAll ? 'Владелец — всем сотрудникам' : 'Владелец'}</b>}
                  <span>{m.text}</span>
                  <time className="num">{dueLong(m.at)}</time>
                </li>
              ))}
              <li ref={end} aria-hidden="true" />
            </ul>
          )}
        </div>
        <form className="form-body" onSubmit={submit} style={{ borderTop: '1px solid var(--line)' }}>
          <div className="g-field">
            <label htmlFor="owner-reply">Написать владельцу</label>
            <textarea className="g-input" id="owner-reply" rows={3} maxLength={2000} style={{ height: 'auto', padding: '10px 12px', resize: 'vertical', font: 'inherit' }}
              value={text} onChange={(e) => setText(e.target.value)} />
          </div>
          {error && <div className="g-note g-note--err" role="alert"><span>{error}</span></div>}
          <button className="g-btn g-btn--primary" type="submit" disabled={busy || !text.trim()}>{busy ? 'Отправляем…' : 'Отправить'}</button>
        </form>
      </section>
    </>
  );
}
