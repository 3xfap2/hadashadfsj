import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { ChatMessage } from './api';
import { clock } from './format';

const KEY = 'ostatok.chat';
const PHONE = /^\+?[\d\s()-]{10,}$/;
const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

const json = (body: unknown): RequestInit => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

function saved(): string | null {
  try {
    return localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

function save(id: string | null) {
  try {
    if (id) localStorage.setItem(KEY, id);
    else localStorage.removeItem(KEY);
  } catch {
    // Без хранилища диалог живёт, пока открыта страница.
  }
}

/** Лента сообщений; mine — чьи сообщения стоят справа: посетителя на лендинге, сотрудника в кабинете. */
export function Messages({ items, mine }: { items: ChatMessage[]; mine: 'visitor' | 'staff' }) {
  const end = useRef<HTMLLIElement>(null);
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'nearest' });
  }, [items.length]);
  return (
    <ul className="msgs">
      {items.map((m) => (
        <li key={m.id} className={`msg${m.fromStaff === (mine === 'staff') ? ' msg--mine' : ''}`}>
          <span>{m.text}</span>
          <time className="num">{clock(m.at)}</time>
        </li>
      ))}
      <li ref={end} aria-hidden="true" />
    </ul>
  );
}

/** Кнопка «Задать вопрос» на лендинге: посетитель пишет без входа, отвечает сотрудник из кабинета. */
export function LandingChat() {
  const [open, setOpen] = useState(false);
  const [chatId, setChatId] = useState(saved);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [seen, setSeen] = useState(0);
  const [form, setForm] = useState({ name: '', contact: '', text: '' });
  const [tried, setTried] = useState(false);
  const [state, setState] = useState<'idle' | 'sending' | 'failed' | 'limited'>('idle');

  // Пока диалог начат, проверяем ответы: часто при открытом окне и изредка при закрытом.
  useEffect(() => {
    if (!chatId) return;
    const load = () =>
      fetch(`/api/chats/${chatId}`)
        .then(async (r) => {
          if (r.ok) setMessages((await r.json()).messages);
          // Диалога больше нет на сервере — начинаем заново.
          else if (r.status === 404) {
            save(null);
            setChatId(null);
            setMessages([]);
          }
        })
        .catch(() => {});
    void load();
    const timer = setInterval(load, open ? 4000 : 20_000);
    return () => clearInterval(timer);
  }, [chatId, open]);

  useEffect(() => {
    if (open) setSeen(messages.length);
  }, [open, messages.length]);

  const fresh = messages.slice(seen).filter((m) => m.fromStaff).length;
  const contact = form.contact.trim();
  const bad = {
    name: tried && !form.name.trim(),
    contact: tried && !(PHONE.test(contact) || EMAIL.test(contact)),
    text: tried && !form.text.trim(),
  };
  const field = (invalid: boolean) => `g-field${invalid ? ' g-field--error' : ''}`;
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [key]: e.target.value }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const text = form.text.trim();
    if (chatId) {
      if (!text) return;
    } else {
      setTried(true);
      if (!form.name.trim() || !text || !(PHONE.test(contact) || EMAIL.test(contact))) return;
    }
    setState('sending');
    const response = await fetch(chatId ? `/api/chats/${chatId}/messages` : '/api/chats', json(chatId ? { text } : form)).catch(() => null);
    // Сервер принимает не больше десяти сообщений в минуту с одного адреса.
    if (!response?.ok) return setState(response?.status === 429 ? 'limited' : 'failed');
    const body = await response.json();
    if (chatId) setMessages((m) => [...m, body]);
    else {
      save(body.id);
      setChatId(body.id);
      setMessages(body.messages);
    }
    setForm((f) => ({ ...f, text: '' }));
    setState('idle');
  };

  if (!open) {
    return (
      <button className="g-btn g-btn--primary ask-btn" type="button" onClick={() => setOpen(true)}>
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5h16v11H9l-5 4z" /></svg>
        Задать вопрос
        {fresh > 0 && <span className="ask-btn__new num" aria-label={`новых ответов: ${fresh}`}>{fresh}</span>}
      </button>
    );
  }

  const failed = (state === 'failed' || state === 'limited') && (
    <span className="g-error-text" role="alert">
      {state === 'limited'
        ? 'Слишком много сообщений подряд. Подождите минуту и отправьте ещё раз.'
        : 'Сообщение не отправлено. Проверьте соединение и попробуйте ещё раз.'}
    </span>
  );

  return (
    <section className="ask" aria-label="Вопрос по продукту">
      <header className="ask__head">
        <div>
          <h2>Вопрос по продукту</h2>
          <span className="muted small">Отвечает человек из команды Остатка</span>
        </div>
        <button className="ask__close" type="button" aria-label="Свернуть" onClick={() => setOpen(false)}>
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
        </button>
      </header>

      {chatId ? (
        <>
          <div className="ask__body">
            <Messages items={messages} mine="visitor" />
            {!messages.some((m) => m.fromStaff) && (
              <p className="muted small ask__wait">Вопрос отправлен. Ответ появится здесь; если закроете страницу, свяжемся по вашему контакту.</p>
            )}
          </div>
          <form className="ask__send" onSubmit={submit}>
            {failed}
            <div className="ask__line">
              <input className="g-input" aria-label="Сообщение" placeholder="Сообщение" maxLength={1000} value={form.text} onChange={set('text')} />
              <button className="g-btn g-btn--primary" type="submit" disabled={state === 'sending'}>Отправить</button>
            </div>
          </form>
        </>
      ) : (
        <form className="ask__form" onSubmit={submit} noValidate>
          <div className={field(bad.name)}>
            <label htmlFor="ask-name">Имя</label>
            <input className="g-input" id="ask-name" autoComplete="name" value={form.name} onChange={set('name')} aria-invalid={bad.name} />
            {bad.name && <span className="g-error-text">Как к вам обращаться?</span>}
          </div>
          <div className={field(bad.contact)}>
            <label htmlFor="ask-contact">Телефон или почта</label>
            <input className="g-input" id="ask-contact" value={form.contact} onChange={set('contact')} aria-invalid={bad.contact} />
            {bad.contact
              ? <span className="g-error-text">Укажите телефон, например +7 900 000-00-00, или почту</span>
              : <span className="g-hint">Чтобы ответить, если вы закроете страницу.</span>}
          </div>
          <div className={field(bad.text)}>
            <label htmlFor="ask-text">Ваш вопрос</label>
            <textarea className="g-input" id="ask-text" maxLength={1000} value={form.text} onChange={set('text')} aria-invalid={bad.text} />
            {bad.text && <span className="g-error-text">Напишите вопрос</span>}
          </div>
          {failed}
          <button className="g-btn g-btn--primary g-btn--block" type="submit" disabled={state === 'sending'}>
            {state === 'sending' ? 'Отправляем…' : 'Отправить'}
          </button>
        </form>
      )}
    </section>
  );
}
