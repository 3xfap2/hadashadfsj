import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { send, useAction, useApi, type ChatDetails, type ChatRow, type LeadRow } from '../api';
import { dueLong } from '../format';
import { Fail, Messages, Wait } from '../ui';

/** Вопросы посетителей лендинга: слева диалоги, справа переписка, ответ и связь с заявкой. */
export function Chats() {
  const { id } = useParams();
  const chats = useApi<ChatRow[]>('/crm/chats');
  if (!chats.data) return <Wait error={chats.error} what="диалоги" reload={chats.reload} />;
  const selected = chats.data.find((c) => c.id === id);

  return (
    <>
      <div className="g-title"><h1>Диалоги</h1><span className="sub">чат на сайте; ответ сразу появляется у человека на странице</span></div>

      <div className={`chats${id ? ' chats--open' : ''}`}>
        <section className="g-panel chats__list" aria-label="Диалоги">
          {chats.data.length === 0 ? (
            <p className="g-empty"><strong>Вопросов пока нет</strong>Они появятся здесь, когда посетитель напишет в чат на сайте.</p>
          ) : (
            <ul>
              {chats.data.map((c) => (
                <li key={c.id}>
                  <Link className="chat-row" to={`/chats/${c.id}`} aria-current={c.id === id}>
                    <span className="g-name">{c.name}</span>
                    <span className="muted small num">{dueLong(c.lastMessageAt)}</span>
                    <span className="muted small chat-row__text">{c.lastFromStaff && 'Вы: '}{c.lastText}</span>
                    {c.unread > 0 && <span className="g-tag g-tag--red num">{c.unread}</span>}
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        {id ? (
          <Dialog key={id} id={id} leadId={selected?.leadId ?? null} />
        ) : (
          <section className="g-panel chats__dialog chats__dialog--empty">
            <p className="g-empty"><strong>Выберите диалог</strong>Переписка откроется здесь.</p>
          </section>
        )}
      </div>
    </>
  );
}

function Dialog({ id, leadId }: { id: string; leadId: number | null }) {
  const chat = useApi<ChatDetails>(`/support/chats/${id}`);
  const [text, setText] = useState('');
  const { busy, error, run } = useAction();
  const count = chat.data?.messages.length ?? 0;

  // Открытый диалог считается прочитанным, в том числе когда в него приходит новое сообщение.
  useEffect(() => {
    if (count > 0) void send('POST', `/support/chats/${id}/read`).catch(() => {});
  }, [id, count]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (text.trim()) void run(() => send('POST', `/support/chats/${id}/messages`, { text }), () => setText(''));
  };

  return (
    <section className="g-panel chats__dialog" aria-label="Переписка">
      <div className="g-panel__head chats__head">
        <Link className="back" to="/chats">← Диалоги</Link>
        <h2>{chat.data?.name ?? '…'}</h2>
        {chat.data && <span className="end num pick">{chat.data.contact}</span>}
      </div>
      <LeadLink id={id} leadId={leadId} />
      <div className="chats__msgs">
        {chat.data ? <Messages items={chat.data.messages} /> : <Wait error={chat.error} what="переписку" reload={chat.reload} />}
      </div>
      <form className="g-panel__foot stack" onSubmit={submit}>
        <Fail text={error} />
        <div className="line">
          <input className="g-input" aria-label="Ответ" placeholder="Ответ" maxLength={1000} value={text} onChange={(e) => setText(e.target.value)} />
          <button className="g-btn g-btn--primary" type="submit" disabled={busy || !text.trim()}>Ответить</button>
        </div>
      </form>
    </section>
  );
}

/** Связь диалога с заявкой: привязать к существующей, создать новую из диалога или отвязать. */
function LeadLink({ id, leadId }: { id: string; leadId: number | null }) {
  const leads = useApi<LeadRow[]>('/crm/leads', 60_000);
  const [picked, setPicked] = useState('');
  const { busy, error, run } = useAction();
  const navigate = useNavigate();
  const lead = leads.data?.find((l) => l.id === leadId);

  return (
    <div className="chats__lead">
      {leadId !== null ? (
        <>
          <span>Заявка: <Link className="g-name" to={`/leads/${leadId}`}>{lead ? `${lead.organization} · ${lead.name}` : `№ ${leadId}`}</Link></span>
          <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => void run(() => send('PUT', `/crm/chats/${id}/lead`, { leadId: null }))}>Отвязать</button>
        </>
      ) : (
        <>
          <select className="g-input" aria-label="Заявка" value={picked} onChange={(e) => setPicked(e.target.value)}>
            <option value="">Заявка не выбрана</option>
            {leads.data?.map((l) => <option key={l.id} value={l.id}>№ {l.id} · {l.organization} · {l.name}</option>)}
          </select>
          <button className="g-btn g-btn--sm" type="button" disabled={busy || !picked} onClick={() => void run(() => send('PUT', `/crm/chats/${id}/lead`, { leadId: Number(picked) }))}>Привязать</button>
          <button className="g-btn g-btn--sm" type="button" disabled={busy}
            onClick={() => void run(() => send('POST', `/crm/chats/${id}/lead`), async (response) => navigate(`/leads/${((await response.json()) as LeadRow).id}`))}>
            Создать заявку
          </button>
        </>
      )}
      <Fail text={error} />
    </div>
  );
}
