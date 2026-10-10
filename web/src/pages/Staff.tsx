import { useState, type FormEvent } from 'react';
import { send, useApi, type Me, type StaffRow } from '../api';

const ROLES: { id: StaffRow['role']; title: string; can: string }[] = [
  { id: 'Waiter', title: 'Официант', can: 'оформляет заказы и закрывает столы; видит меню, стоп-лист и горячие предложения' },
  { id: 'Cook', title: 'Повар', can: 'готовит заготовки, ставит блюдо в стоп вручную' },
  { id: 'Storekeeper', title: 'Кладовщик', can: 'принимает поставки, списывает партии, печатает этикетки, собирает заявку поставщику' },
  { id: 'Chef', title: 'Шеф-повар', can: 'склад, заготовки, закупки, аналитика, справочники и настройка горячих предложений' },
  { id: 'Manager', title: 'Управляющий', can: 'всё, что шеф-повар, и сотрудники ресторана' },
];
const roleTitle = (role: StaffRow['role']) => ROLES.find((r) => r.id === role)!.title;

/** Сотрудники ресторана: управляющий заводит учётные записи и назначает роли. */
export function Staff() {
  const staff = useApi<StaffRow[]>('/users', 60_000);
  const me = useApi<Me>('/me', 300_000).data;
  const [form, setForm] = useState({ login: '', password: '', title: '', role: 'Waiter' as StaffRow['role'] });
  const [note, setNote] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  if (staff.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить сотрудников</h3><span>{staff.error}</span></div>;
  if (!staff.data) return <p className="muted">Загружаем сотрудников…</p>;

  const problem = async (response: Response) => {
    const body = await response.json().catch(() => ({}));
    return Object.values((body.errors ?? {}) as Record<string, string[]>).flat().join(' ') || body.detail || `Сервер ответил ошибкой ${response.status}.`;
  };

  const add = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    const title = form.title.trim() || roleTitle(form.role);
    const response = await send('POST', '/users', { ...form, title });
    if (response.ok) {
      setNote({ kind: 'ok', text: `Учётная запись «${form.login.trim().toLowerCase()}» создана. Передайте сотруднику логин и пароль.` });
      setForm({ login: '', password: '', title: '', role: form.role });
      await staff.reload();
    } else {
      setNote({ kind: 'err', text: await problem(response) });
    }
    setBusy(false);
  };

  const remove = async (user: StaffRow) => {
    setBusy(true);
    const response = await send('DELETE', `/users/${user.id}`);
    setNote(response.ok ? null : { kind: 'err', text: await problem(response) });
    await staff.reload();
    setBusy(false);
  };

  return (
    <>
      <div className="buy-head">
        <h1>Сотрудники</h1>
        <p className="lead-text">{me ? `${me.restaurant}. ` : ''}У каждой роли своя точка входа: заказы оформляет официант, поставки принимает кладовщик.</p>
      </div>

      <div className="sup-row">
        <section className="sup-main g-panel" aria-labelledby="staff-h">
          <div className="g-panel__head"><h2 id="staff-h">Учётные записи</h2><span className="end">{staff.data.length}</span></div>
          <div className="g-table-wrap">
            <table className="g-table">
              <thead><tr><th>Логин</th><th>Должность</th><th>Роль</th><th /></tr></thead>
              <tbody>
                {staff.data.map((u) => (
                  <tr key={u.id}>
                    <td className="g-name">{u.login}</td>
                    <td>{u.title}</td>
                    <td>{roleTitle(u.role)}</td>
                    <td className="r">
                      {u.login === me?.login
                        ? <span className="muted small">это вы</span>
                        : <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => remove(u)}>Удалить</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="form-body staff-roles">
            <h3>Что может каждая роль</h3>
            <ul>
              {ROLES.map((r) => <li key={r.id}><b>{r.title}</b> — {r.can}.</li>)}
            </ul>
          </div>
        </section>

        <aside className="sup-side cat-side">
          <form className="g-panel" onSubmit={add} noValidate>
            <div className="g-panel__head"><h2>Новый сотрудник</h2></div>
            <div className="form-body">
              <div className="g-field">
                <label htmlFor="s-role">Роль</label>
                <select className="g-input" id="s-role" value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as StaffRow['role'] })}>
                  {ROLES.map((r) => <option key={r.id} value={r.id}>{r.title}</option>)}
                </select>
              </div>
              <div className="g-field">
                <label htmlFor="s-title">Должность <span className="muted" style={{ fontWeight: 400 }}>— необязательно</span></label>
                <input className="g-input" id="s-title" placeholder={roleTitle(form.role)} value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} />
              </div>
              <div className="g-field">
                <label htmlFor="s-login">Логин</label>
                <input className="g-input" id="s-login" autoCapitalize="none" autoComplete="off" value={form.login} onChange={(e) => setForm({ ...form, login: e.target.value })} />
                <span className="g-hint">Латинские буквы, цифры, точка, дефис; от 3 символов.</span>
              </div>
              <div className="g-field">
                <label htmlFor="s-password">Пароль</label>
                <input className="g-input" id="s-password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
                <span className="g-hint">Не короче 8 символов.</span>
              </div>
              {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
            </div>
            <div className="g-panel__foot cat-foot">
              <span />
              <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Создать</button>
            </div>
          </form>
        </aside>
      </div>
    </>
  );
}
