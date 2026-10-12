import { useState, type FormEvent } from 'react';
import { enterRestaurant, post, useApi, type MyRestaurant } from '../api';
import { money, plural } from '../format';

/**
 * Рестораны владельца: сводка по каждому, переход между ними и добавление нового.
 * В каждом ресторане свои продукты, поставки, блюда и сотрудники — при переходе данные не смешиваются.
 */
export function MyRestaurants() {
  const list = useApi<MyRestaurant[]>('/my/restaurants', 60_000);
  const [name, setName] = useState('');
  const [note, setNote] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  if (!list.data) return null;

  const enter = async (id: string) => {
    setBusy(true);
    const error = await enterRestaurant(id).catch((e: Error) => e.message);
    // При удаче страница перезагружается уже в другом ресторане; сюда доходим только при ошибке.
    if (error) setNote({ kind: 'err', text: error });
    setBusy(false);
  };

  const add = async (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setBusy(true);
    try {
      const response = await post('/my/restaurants', { name });
      if (response.ok) {
        setName('');
        setNote({ kind: 'ok', text: 'Ресторан добавлен. Перейдите в него, чтобы завести продукты, блюда и сотрудников.' });
        await list.reload();
      } else {
        const problem = await response.json().catch(() => ({}));
        const messages = Object.values((problem.errors ?? {}) as Record<string, string[]>).flat();
        setNote({ kind: 'err', text: messages.join(' ') || problem.detail || `Сервер ответил ошибкой ${response.status}.` });
      }
    } catch (err) {
      setNote({ kind: 'err', text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="g-panel" aria-labelledby="mine-h">
      <div className="g-panel__head"><h2 id="mine-h">Мои рестораны</h2><span className="end num">{list.data.length}</span></div>
      <ul className="g-rows mine">
        {list.data.map((r) => (
          <li key={r.id} className="mine__row">
            <span className="mine__text">
              <span className="g-name">{r.name}</span>
              <span className="muted small">
                {r.stopped > 0 ? `${plural(r.stopped, 'блюдо', 'блюда', 'блюд')} в стопе` : 'стопов нет'} · истекает на <span className="num">{money(r.atRiskValue)}</span>
              </span>
            </span>
            {r.current
              ? <span className="g-tag">Вы здесь</span>
              : <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => enter(r.id)}>Перейти</button>}
          </li>
        ))}
      </ul>
      <form className="form-body" onSubmit={add} noValidate>
        <div className="g-field">
          <label htmlFor="mine-name">Добавить ресторан</label>
          <input className="g-input" id="mine-name" placeholder="Название" maxLength={120} value={name} onChange={(e) => setName(e.target.value)} />
          <span className="g-hint">Новый ресторан создаётся пустым: в нём будут свои продукты, поставщики, блюда и сотрудники.</span>
        </div>
        {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
        <button className="g-btn" type="submit" disabled={busy || !name.trim()}>Добавить</button>
      </form>
    </section>
  );
}
