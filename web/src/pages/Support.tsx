import { useEffect, useState, type FormEvent } from 'react';
import { NavLink } from 'react-router-dom';
import { post, useApi, type ChatDetails, type ChatRow } from '../api';
import { dueLong } from '../format';
import { Messages } from '../LandingChat';

/** Значок в шапке: ведёт в «Обращения» и показывает, сколько вопросов ждут ответа. */
export function SupportLink() {
  const chats = useApi<ChatRow[]>('/support/chats');
  const waiting = chats.data?.reduce((sum, c) => sum + c.unread, 0) ?? 0;
  return (
    <NavLink className="alerts__bell" to="/app/support" aria-label={waiting > 0 ? `Обращения, без ответа: ${waiting}` : 'Обращения'}>
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16v11H9l-5 4z" /></svg>
      {waiting > 0 && <span className="alerts__count num">{waiting > 99 ? '99+' : waiting}</span>}
    </NavLink>
  );
}

/** Вопросы посетителей лендинга: слева диалоги, справа переписка и ответ. */
export function Support() {
  const chats = useApi<ChatRow[]>('/support/chats');
  const [selected, setSelected] = useState<string | null>(null);

  if (chats.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить обращения</h3><span>{chats.error}</span></div>;
  if (!chats.data) return <p className="muted">Загружаем обращения…</p>;

  return (
    <>
      <div className="buy-head">
        <h1>Обращения</h1>
        <p className="lead-text">Вопросы, которые посетители задают в чате на сайте. Ответ сразу появляется у человека на странице.</p>
      </div>

      <div className={`support${selected ? ' support--open' : ''}`}>
        <section className="g-panel support__list" aria-labelledby="chats-h">
          <div className="g-panel__head">
            <h2 id="chats-h">Диалоги</h2>
            <span className="end">{chats.data.length}</span>
          </div>
          {chats.data.length === 0 ? (
            <p className="g-empty"><strong>Вопросов пока нет</strong>Они появятся здесь, когда посетитель напишет в чат на сайте.</p>
          ) : (
            <ul>
              {chats.data.map((c) => (
                <li key={c.id}>
                  <button className="chat-row" type="button" aria-current={selected === c.id} onClick={() => setSelected(c.id)}>
                    <span className="g-name">{c.name}</span>
                    <span className="muted small num">{dueLong(c.lastMessageAt)}</span>
                    <span className="muted small chat-row__text">{c.lastFromStaff && 'Вы: '}{c.lastText}</span>
                    {c.unread > 0 && <span className="g-tag g-tag--red num">{c.unread}</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>

        {selected ? (
          <Dialog key={selected} id={selected} onBack={() => setSelected(null)} />
        ) : (
          <section className="g-panel support__dialog support__dialog--empty">
            <p className="g-empty"><strong>Выберите диалог</strong>Переписка откроется здесь.</p>
          </section>
        )}
      </div>
    </>
  );
}

function Dialog({ id, onBack }: { id: string; onBack: () => void }) {
  const chat = useApi<ChatDetails>(`/support/chats/${id}`);
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const count = chat.data?.messages.length ?? 0;

  // Открытый диалог считается прочитанным, в том числе когда в него приходит новое сообщение.
  useEffect(() => {
    if (count > 0) void post(`/support/chats/${id}/read`);
  }, [id, count]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;
    setBusy(true);
    const response = await post(`/support/chats/${id}/messages`, { text });
    setBusy(false);
    if (!response.ok) return setError(`Ответ не отправлен: сервер ответил ошибкой ${response.status}.`);
    setError('');
    setText('');
    await chat.reload();
  };

  return (
    <section className="g-panel support__dialog" aria-label="Переписка">
      <div className="g-panel__head">
        <button className="support__back" type="button" onClick={onBack}>← Диалоги</button>
        <h2>{chat.data?.name ?? '…'}</h2>
        {chat.data && <span className="end num" style={{ userSelect: 'all' }}>{chat.data.contact}</span>}
      </div>
      <div className="support__msgs">
        {chat.error ? <p className="g-empty"><strong>Не удалось загрузить переписку</strong>{chat.error}</p> : <Messages items={chat.data?.messages ?? []} mine="staff" />}
      </div>
      <form className="g-panel__foot support__send" onSubmit={submit}>
        {error && <span className="g-error-text" role="alert">{error}</span>}
        <div className="ask__line">
          <input className="g-input" aria-label="Ответ" placeholder="Ответ" maxLength={1000} value={text} onChange={(e) => setText(e.target.value)} />
          <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Ответить</button>
        </div>
      </form>
    </section>
  );
}
