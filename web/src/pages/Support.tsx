import { useEffect, useState, type FormEvent } from 'react';
import { NavLink } from 'react-router-dom';
import { post, send, useApi, type ChatDetails, type ChatRow, type LeadRow, type NewRestaurant } from '../api';
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

      <div className="sup-row">
        <Leads />
        <aside className="sup-side cat-side"><NewRestaurantForm /></aside>
      </div>
    </>
  );
}

/** Заявки на демо с лендинга: по ним мы связываемся с рестораном и открываем ему кабинет. */
function Leads() {
  const leads = useApi<LeadRow[]>('/leads', 60_000);
  return (
    <section className="sup-main g-panel" aria-labelledby="leads-h">
      <div className="g-panel__head">
        <h2 id="leads-h">Заявки на демо</h2>
        <span className="end">{leads.data ? leads.data.length : ''}</span>
      </div>
      {leads.error ? (
        <p className="g-empty"><strong>Не удалось загрузить заявки</strong>{leads.error}</p>
      ) : !leads.data ? (
        <p className="g-empty">Загружаем заявки…</p>
      ) : leads.data.length === 0 ? (
        <p className="g-empty"><strong>Заявок пока нет</strong>Они появятся, когда посетитель заполнит форму «Запросить демо».</p>
      ) : (
        <div className="g-table-wrap">
          <table className="g-table leads">
            <thead><tr><th>Когда</th><th>Кто</th><th>Ресторан</th><th>Контакт</th><th className="r">Точек</th><th>Комментарий</th></tr></thead>
            <tbody>
              {leads.data.map((l) => (
                <tr key={l.id}>
                  <td className="num muted">{dueLong(l.createdAt)}</td>
                  <td className="g-name">{l.name}</td>
                  <td>{l.organization}</td>
                  <td className="num" style={{ userSelect: 'all' }}>{l.contact}</td>
                  <td className="r num">{l.points}</td>
                  <td className="muted">{l.comment ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/** Кабинет новому ресторану открываем мы: система выдаёт пароль управляющего, его передают клиенту. */
function NewRestaurantForm() {
  const [form, setForm] = useState({ restaurant: '', login: '' });
  const [created, setCreated] = useState<NewRestaurant | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      const response = await send('POST', '/restaurants', form);
      if (response.ok) {
        setCreated(await response.json());
        setForm({ restaurant: '', login: '' });
      } else {
        const problem = await response.json().catch(() => ({}));
        setError(problem.detail || `Сервер ответил ошибкой ${response.status}.`);
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="g-panel" onSubmit={submit} noValidate>
      <div className="g-panel__head"><h2>Новый ресторан</h2><span className="end">кабинет для клиента</span></div>
      <div className="form-body">
        <div className="g-field">
          <label htmlFor="nr-name">Название ресторана</label>
          <input className="g-input" id="nr-name" value={form.restaurant} onChange={(e) => setForm({ ...form, restaurant: e.target.value })} />
        </div>
        <div className="g-field">
          <label htmlFor="nr-login">Логин управляющего</label>
          <input className="g-input" id="nr-login" autoCapitalize="none" autoComplete="off" value={form.login} onChange={(e) => setForm({ ...form, login: e.target.value })} />
          <span className="g-hint">Латинские буквы и цифры, например trattoria.admin. Пароль система выдаст сама.</span>
        </div>
        {error && <div className="g-note g-note--err" role="alert"><span>{error}</span></div>}
        {created && (
          <div className="g-note g-note--ok" role="status">
            <h3>Ресторан «{created.name}» создан</h3>
            <span>Логин: <b className="num" style={{ userSelect: 'all' }}>{created.login}</b></span>
            <span>Пароль: <b className="num" style={{ userSelect: 'all' }}>{created.password}</b></span>
            <span className="muted small">Пароль показан один раз — передайте его клиенту сейчас.</span>
          </div>
        )}
      </div>
      <div className="g-panel__foot cat-foot">
        <span />
        <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Создать</button>
      </div>
    </form>
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
