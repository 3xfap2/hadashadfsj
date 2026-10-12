import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { send, useAction, type ChatMessage, type Credentials, type LeadStatus, type Note, type Summary, type Task } from './api';
import { dueLong, toLocalInput } from './format';

/** Воронка по порядку: key — имя счётчика в сводке. */
export const STATUSES: { id: LeadStatus; key: keyof Summary['leads']; title: string }[] = [
  { id: 'New', key: 'new', title: 'Новые' },
  { id: 'Contacted', key: 'contacted', title: 'Связались' },
  { id: 'Demo', key: 'demo', title: 'Демо' },
  { id: 'Won', key: 'won', title: 'Подключены' },
  { id: 'Lost', key: 'lost', title: 'Отказ' },
];

/** К чему относится заметка или задача: к заявке или к ресторану. */
export type Target = { leadId: number } | { restaurantId: string };

/** Загрузка и ошибка экрана: пока данных нет, вместо него показывается одно из двух. */
export function Wait({ error, what, reload }: { error: string | null; what: string; reload: () => unknown }) {
  if (!error) return <p className="muted" role="status">Загружаем {what}…</p>;
  return (
    <div className="g-note g-note--err" role="alert">
      <h3>Не удалось загрузить {what}</h3>
      <span>{error}</span>
      <button className="g-btn g-btn--sm" type="button" onClick={() => void reload()}>Повторить</button>
    </div>
  );
}

export const Fail = ({ text }: { text: string }) => (text ? <span className="g-error-text" role="alert">{text}</span> : null);

/** Опасное действие: первая кнопка только спрашивает, выполняет вторая. */
export function Confirm({ label, ask, yes, busy, onConfirm }: { label: string; ask: string; yes: string; busy: boolean; onConfirm: () => unknown }) {
  const [open, setOpen] = useState(false);
  if (!open) return <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => setOpen(true)}>{label}</button>;
  return (
    <span className="confirm" role="group" aria-label={ask}>
      <span className="hd">{ask}</span>
      <button className="g-btn g-btn--sm confirm__yes" type="button" disabled={busy} onClick={async () => { await onConfirm(); setOpen(false); }}>{yes}</button>
      <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => setOpen(false)}>Отмена</button>
    </span>
  );
}

/** Логин и пароль, которые сервер выдал только что: второй раз их получить нельзя. */
export function Secret({ title, value, onClose }: { title: string; value: Credentials; onClose: () => void }) {
  return (
    <div className="g-note g-note--ok" role="status">
      <h3>{title}</h3>
      <span>Логин: <b className="num pick">{value.login}</b></span>
      <span>Пароль: <b className="num pick">{value.password}</b></span>
      <span className="small">Пароль показан один раз — передайте его клиенту сейчас. Потом его можно только сбросить.</span>
      <button className="g-btn g-btn--sm" type="button" onClick={onClose}>Я передал пароль, скрыть</button>
    </div>
  );
}

/** Лента сообщений диалога: ответы команды справа. */
export function Messages({ items }: { items: ChatMessage[] }) {
  const end = useRef<HTMLLIElement>(null);
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'nearest' });
  }, [items.length]);
  return (
    <ul className="msgs">
      {items.map((m) => (
        <li key={m.id} className={`msg${m.fromStaff ? ' msg--mine' : ''}`}>
          <span>{m.text}</span>
          <time className="num">{dueLong(m.at)}</time>
        </li>
      ))}
      <li ref={end} aria-hidden="true" />
    </ul>
  );
}

export function Notes({ items, target }: { items: Note[]; target: Target }) {
  const [text, setText] = useState('');
  const { busy, error, run } = useAction();

  const add = (e: FormEvent) => {
    e.preventDefault();
    if (text.trim()) void run(() => send('POST', '/crm/notes', { ...target, text }), () => setText(''));
  };

  return (
    <section className="g-panel" aria-label="Заметки">
      <div className="g-panel__head"><h2>Заметки</h2><span className="end num">{items.length}</span></div>
      {items.length === 0 ? (
        <p className="g-empty">Заметок нет. Запишите, о чём договорились.</p>
      ) : (
        <ul className="notes">
          {items.map((n) => (
            <li key={n.id}>
              <p className="hd notes__text">{n.text}</p>
              <div className="notes__meta">
                <span className="muted small">{n.author} · <span className="num">{dueLong(n.createdAt)}</span></span>
                <Confirm label="Удалить" ask="Удалить заметку?" yes="Удалить" busy={busy} onConfirm={() => run(() => send('DELETE', `/crm/notes/${n.id}`))} />
              </div>
            </li>
          ))}
        </ul>
      )}
      <form className="g-panel__foot stack" onSubmit={add}>
        <textarea className="g-input" aria-label="Новая заметка" placeholder="Новая заметка" maxLength={2000} value={text} onChange={(e) => setText(e.target.value)} />
        <Fail text={error} />
        <button className="g-btn g-btn--primary" type="submit" disabled={busy || !text.trim()}>Добавить заметку</button>
      </form>
    </section>
  );
}

/** Поля задачи: одна форма и для новой задачи, и для правки. */
function TaskForm({ task, busy, submit, onSubmit, onCancel }: {
  task?: Task;
  busy: boolean;
  submit: string;
  onSubmit: (value: { title: string; dueAt: string }, reset: () => void) => void;
  onCancel?: () => void;
}) {
  // Новой задаче по умолчанию — завтра в это же время.
  const initial = () => ({ title: task?.title ?? '', dueAt: toLocalInput(task ? Date.parse(task.dueAt) : Date.now() + 86_400_000) });
  const [form, setForm] = useState(initial);
  const ready = form.title.trim() && form.dueAt;

  const submitForm = (e: FormEvent) => {
    e.preventDefault();
    if (ready) onSubmit({ title: form.title, dueAt: new Date(form.dueAt).toISOString() }, () => setForm(initial()));
  };

  return (
    <form className="task-form" onSubmit={submitForm}>
      <input className="g-input" aria-label="Что сделать" placeholder="Что сделать" maxLength={200} value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} />
      <input className="g-input num" type="datetime-local" aria-label="Срок" value={form.dueAt} onChange={(e) => setForm({ ...form, dueAt: e.target.value })} />
      <button className="g-btn g-btn--primary" type="submit" disabled={busy || !ready}>{submit}</button>
      {onCancel && <button className="g-btn" type="button" disabled={busy} onClick={onCancel}>Отмена</button>}
    </form>
  );
}

function TaskItem({ task, link }: { task: Task; link: boolean }) {
  const [editing, setEditing] = useState(false);
  const { busy, error, run } = useAction();
  const overdue = !task.doneAt && Date.parse(task.dueAt) < Date.now();
  const put = (value: { title: string; dueAt: string }, done: boolean) => send('PUT', `/crm/tasks/${task.id}`, { ...value, done });

  if (editing) {
    return (
      <li className="task">
        <TaskForm task={task} busy={busy} submit="Сохранить" onCancel={() => setEditing(false)}
          onSubmit={(value) => void run(() => put(value, task.doneAt !== null), () => setEditing(false))} />
        <Fail text={error} />
      </li>
    );
  }

  return (
    <li className={`task${task.doneAt ? ' task--done' : ''}`}>
      <div className="task__text">
        <span className="g-name">{task.title}</span>
        <span className="small">
          {task.doneAt
            ? <span className="muted">сделано <span className="num">{dueLong(task.doneAt)}</span></span>
            : overdue
              ? <span className="g-tag g-tag--red">просрочено · <span className="num">{dueLong(task.dueAt)}</span></span>
              : <span className="muted">срок <span className="num">{dueLong(task.dueAt)}</span></span>}
          {link && task.leadId !== null && <> · <Link to={`/leads/${task.leadId}`}>заявка: {task.leadName}</Link></>}
          {link && task.restaurantId && <> · <Link to={`/restaurants/${task.restaurantId}`}>ресторан: {task.restaurantName}</Link></>}
        </span>
        <Fail text={error} />
      </div>
      <div className="acts">
        <button className={`g-btn g-btn--sm${task.doneAt ? '' : ' g-btn--primary'}`} type="button" disabled={busy} onClick={() => void run(() => put({ title: task.title, dueAt: task.dueAt }, !task.doneAt))}>
          {task.doneAt ? 'Вернуть в работу' : 'Сделано'}
        </button>
        <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => setEditing(true)}>Изменить</button>
        <Confirm label="Удалить" ask="Удалить задачу?" yes="Удалить" busy={busy} onConfirm={() => run(() => send('DELETE', `/crm/tasks/${task.id}`))} />
      </div>
    </li>
  );
}

/** Список задач; target — к чему привязать новую задачу, без него форма создаёт задачу без привязки. */
export function Tasks({ title, items, target, link = false, canAdd = true, empty, end }: {
  title: string;
  items: Task[];
  target?: Target;
  link?: boolean;
  canAdd?: boolean;
  empty: string;
  end?: ReactNode;
}) {
  const { busy, error, run } = useAction();
  return (
    <section className="g-panel" aria-label={title}>
      <div className="g-panel__head"><h2>{title}</h2><span className="end">{end ?? <span className="num">{items.length}</span>}</span></div>
      {items.length === 0 ? <p className="g-empty">{empty}</p> : <ul className="tasks">{items.map((t) => <TaskItem key={t.id} task={t} link={link} />)}</ul>}
      {canAdd && (
        <div className="g-panel__foot stack">
          <TaskForm busy={busy} submit="Добавить задачу" onSubmit={(value, reset) => void run(() => send('POST', '/crm/tasks', { ...value, ...target }), reset)} />
          <Fail text={error} />
        </div>
      )}
    </section>
  );
}

