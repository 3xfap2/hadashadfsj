import { useEffect, useRef, useState, type FormEvent } from 'react';
import { send, useAction, useApi, type Dialog, type Message, type MyRestaurant, type StaffRow } from '../api';
import { dueLong, roleTitle } from '../format';
import { Fail, Wait } from '../ui';

/** Собеседник: сотрудник ресторана или все сотрудники сразу (employee = null). */
interface Target {
  restaurantId: string;
  restaurant: string;
  employee: string | null;
  name: string;
}

const key = (restaurantId: string, employee: string | null) => `${restaurantId} ${employee ?? '*'}`;

/** Переписка с сотрудниками: слева рестораны и сотрудники, справа диалог. Сотрудник отвечает в кабинете ресторана. */
export function Messages() {
  const mine = useApi<MyRestaurant[]>('/my/restaurants', 300_000);
  const staff = useApi<StaffRow[]>('/owner/staff', 60_000);
  const dialogs = useApi<Dialog[]>('/owner/messages', 15_000);
  const [selected, setSelected] = useState<Target | null>(null);

  if (!mine.data || !staff.data || !dialogs.data) {
    return <Wait error={mine.error ?? staff.error ?? dialogs.error} what="сообщения" reload={() => Promise.all([mine.reload(), staff.reload(), dialogs.reload()])} />;
  }
  const byKey = new Map(dialogs.data.map((d) => [key(d.restaurantId, d.employee), d]));

  return (
    <>
      <div className="g-title"><h1>Сообщения</h1><span className="sub">сотрудник видит их в кабинете ресторана и отвечает там же</span></div>

      <div className={`chats${selected ? ' chats--open' : ''}`}>
        <section className="g-panel chats__list" aria-label="Собеседники">
          <ul>
            {mine.data.map((r) => {
              const people: Target[] = [
                { restaurantId: r.id, restaurant: r.name, employee: null, name: 'Всем сотрудникам' },
                ...staff.data!.filter((s) => s.restaurantId === r.id && !s.owner)
                  .map((s) => ({ restaurantId: r.id, restaurant: r.name, employee: s.login, name: `${s.title || roleTitle(s.role)} · ${s.login}` })),
              ];
              return (
                <li key={r.id}>
                  <h2 className="chats__group">{r.name}</h2>
                  <ul>
                    {people.map((p) => {
                      const d = byKey.get(key(p.restaurantId, p.employee));
                      const current = selected !== null && key(selected.restaurantId, selected.employee) === key(p.restaurantId, p.employee);
                      return (
                        <li key={key(p.restaurantId, p.employee)}>
                          <button className="chat-row" type="button" aria-current={current} onClick={() => setSelected(p)}>
                            <span className="g-name">{p.name}</span>
                            <span className="muted small num">{d ? dueLong(d.lastAt) : ''}</span>
                            <span className="muted small chat-row__text">{d ? `${d.lastFromOwner ? 'Вы: ' : ''}${d.lastText}` : 'Сообщений нет'}</span>
                            {d && d.unread > 0 && <span className="g-tag g-tag--red num">{d.unread}</span>}
                          </button>
                        </li>
                      );
                    })}
                    {people.length === 1 && <li className="muted small chats__none">В ресторане пока нет сотрудников — заведите их в разделе «Сотрудники».</li>}
                  </ul>
                </li>
              );
            })}
          </ul>
        </section>

        {selected ? (
          <Thread key={key(selected.restaurantId, selected.employee)} target={selected} unread={byKey.get(key(selected.restaurantId, selected.employee))?.unread ?? 0} onBack={() => setSelected(null)} />
        ) : (
          <section className="g-panel chats__dialog chats__dialog--empty">
            <p className="g-empty"><strong>Выберите собеседника</strong>Можно написать одному сотруднику или сразу всем сотрудникам ресторана.</p>
          </section>
        )}
      </div>
    </>
  );
}

function Thread({ target, unread, onBack }: { target: Target; unread: number; onBack: () => void }) {
  const query = target.employee ? `?employee=${encodeURIComponent(target.employee)}` : '';
  const thread = useApi<Message[]>(`/owner/messages/${target.restaurantId}${query}`, 10_000);
  const [text, setText] = useState('');
  const { busy, error, run } = useAction();
  const end = useRef<HTMLLIElement>(null);
  const count = thread.data?.length ?? 0;

  // Открытый диалог прочитан, в том числе когда в него приходит новый ответ.
  useEffect(() => {
    if (unread > 0) void send('POST', `/owner/messages/${target.restaurantId}/read${query}`).catch(() => {});
  }, [unread, count, target.restaurantId, query]);
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'nearest' });
  }, [count]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (text.trim()) void run(() => send('POST', '/owner/messages', { restaurantId: target.restaurantId, employee: target.employee, text }), () => setText(''));
  };

  return (
    <section className="g-panel chats__dialog" aria-label="Переписка">
      <div className="g-panel__head chats__head">
        <button className="back" type="button" onClick={onBack}>← Собеседники</button>
        <h2>{target.name}</h2>
        <span className="end">{target.restaurant}</span>
      </div>
      <div className="chats__msgs">
        {!thread.data ? <Wait error={thread.error} what="переписку" reload={thread.reload} /> : thread.data.length === 0 ? (
          <p className="g-empty"><strong>Сообщений пока нет</strong>{target.employee ? 'Напишите первым.' : 'Сообщение увидят все сотрудники ресторана.'}</p>
        ) : (
          <ul className="msgs">
            {thread.data.map((m) => (
              <li key={m.id} className={`msg${m.fromOwner ? ' msg--mine' : ''}`}>
                <span>{m.text}</span>
                <time className="num">{dueLong(m.at)}</time>
              </li>
            ))}
            <li ref={end} aria-hidden="true" />
          </ul>
        )}
      </div>
      <form className="g-panel__foot stack" onSubmit={submit}>
        <Fail text={error} />
        <div className="line">
          <textarea className="g-input chats__reply" aria-label="Сообщение" placeholder={target.employee ? 'Сообщение сотруднику' : 'Сообщение всем сотрудникам ресторана'}
            rows={3} maxLength={2000} value={text} onChange={(e) => setText(e.target.value)} />
          <button className="g-btn g-btn--primary" type="submit" disabled={busy || !text.trim()}>Отправить</button>
        </div>
      </form>
    </section>
  );
}
