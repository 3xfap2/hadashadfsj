import { useState, type FormEvent } from 'react';
import { send, useAction, useApi, type Me, type MyRestaurant, type Role, type StaffRow } from '../api';
import { ROLES, roleTitle } from '../format';
import { Confirm, Fail, Wait } from '../ui';

const EMPTY = { restaurantId: '', login: '', password: '', title: '', role: 'Waiter' as Role };

/** Сотрудники всех ресторанов владельца: завести, сменить роль или пароль, удалить — в выбранном ресторане. */
export function Staff({ me }: { me: Me }) {
  const staff = useApi<StaffRow[]>('/owner/staff', 60_000);
  // Рестораны берутся из «Моих ресторанов»: в только что добавленном ещё нет ни одного сотрудника.
  const mine = useApi<MyRestaurant[]>('/my/restaurants', 300_000);
  const [filter, setFilter] = useState('');
  const [form, setForm] = useState(EMPTY);
  // Сотрудник, которого сейчас правят; null — форма заводит нового.
  const [editing, setEditing] = useState<StaffRow | null>(null);
  const [done, setDone] = useState('');
  const { busy, error, run } = useAction();

  if (!staff.data || !mine.data) return <Wait error={staff.error ?? mine.error} what="сотрудников" reload={() => Promise.all([staff.reload(), mine.reload()])} />;
  const restaurants = mine.data.map((r) => [r.id, r.name] as const);
  const rows = staff.data.filter((s) => !filter || s.restaurantId === filter);
  const restaurantId = editing?.restaurantId ?? (form.restaurantId || filter || restaurants[0]?.[0] || '');

  const startEdit = (user: StaffRow | null) => {
    setEditing(user);
    setDone('');
    setForm(user ? { restaurantId: user.restaurantId, login: user.login, password: '', title: user.title, role: user.role } : EMPTY);
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const title = form.title.trim() || roleTitle(form.role);
    const base = `/owner/restaurants/${restaurantId}/users`;
    void run(
      () => editing
        ? send('PUT', `${base}/${editing.id}`, { title, role: form.role, password: form.password || null })
        : send('POST', base, { login: form.login, password: form.password, title, role: form.role }),
      () => {
        setDone(editing
          ? `Изменения для «${editing.login}» сохранены.${form.password ? ' Передайте сотруднику новый пароль.' : ''}`
          : `Учётная запись «${form.login.trim().toLowerCase()}» создана. Передайте сотруднику логин и пароль.`);
        setEditing(null);
        setForm({ ...EMPTY, restaurantId: form.restaurantId });
      },
    );
  };

  return (
    <>
      <div className="g-title">
        <h1>Сотрудники</h1>
        <span className="sub">{staff.data.length} во всех ресторанах</span>
        <span className="end">
          <select className="g-input" aria-label="Ресторан" value={filter} onChange={(e) => setFilter(e.target.value)}>
            <option value="">Все рестораны</option>
            {restaurants.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
          </select>
        </span>
      </div>

      <div className="cols">
        <section className="g-panel cols__main" aria-label="Сотрудники">
          <div className="g-table-wrap">
            <table className="g-table staff-table stackable">
              <thead><tr><th>Ресторан</th><th>Логин</th><th>Имя и должность</th><th>Роль</th><th /></tr></thead>
              <tbody>
                {rows.map((s) => (
                  <tr key={s.id}>
                    <td data-label="Ресторан">{s.restaurant}</td>
                    <td className="num hd stackable__head">{s.login}</td>
                    <td data-label="Имя и должность">{s.title}{s.owner && <span className="g-tag">это вы</span>}</td>
                    <td data-label="Роль">{roleTitle(s.role)}</td>
                    <td className="r stackable__head">
                      <span className="acts staff-table__acts">
                        <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => startEdit(s)}>Изменить</button>
                        {!s.owner && (
                          <Confirm label="Удалить" ask={`Удалить «${s.login}»?`} yes="Удалить" busy={busy}
                            onConfirm={() => run(() => send('DELETE', `/owner/restaurants/${s.restaurantId}/users/${s.id}`), () => setDone(''))} />
                        )}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {rows.length === 0 && <p className="g-empty"><strong>Сотрудников нет</strong>Заведите первого в форме рядом.</p>}
        </section>

        <section className="g-panel cols__side" aria-labelledby="staff-form-h">
          <div className="g-panel__head"><h2 id="staff-form-h">{editing ? `Изменить «${editing.login}»` : 'Новый сотрудник'}</h2></div>
          <form className="form-body" onSubmit={submit} noValidate>
            <div className="g-field">
              <label htmlFor="st-rest">Ресторан</label>
              <select className="g-input" id="st-rest" value={restaurantId} disabled={editing !== null}
                onChange={(e) => setForm({ ...form, restaurantId: e.target.value })}>
                {restaurants.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
              </select>
            </div>
            {!editing && (
              <div className="g-field">
                <label htmlFor="st-login">Логин</label>
                <input className="g-input" id="st-login" autoCapitalize="none" autoComplete="off" maxLength={40} value={form.login} onChange={(e) => setForm({ ...form, login: e.target.value })} />
                <span className="g-hint">Латинские буквы, цифры, точка, дефис — от 3 до 40 символов.</span>
              </div>
            )}
            <div className="g-field">
              <label htmlFor="st-pass">{editing ? 'Новый пароль' : 'Пароль'}</label>
              <input className="g-input" id="st-pass" type="password" autoComplete="new-password" maxLength={100} value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
              <span className="g-hint">{editing ? 'Пусто — пароль не меняется.' : 'Не короче 8 символов и не из простых.'}</span>
            </div>
            <div className="g-field">
              <label htmlFor="st-title">Имя и должность</label>
              <input className="g-input" id="st-title" maxLength={60} placeholder={roleTitle(form.role)} value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} />
            </div>
            <div className="g-field">
              <label htmlFor="st-role">Роль</label>
              <select className="g-input" id="st-role" value={form.role} disabled={editing?.login === me.login} onChange={(e) => setForm({ ...form, role: e.target.value as Role })}>
                {ROLES.map((r) => <option key={r.id} value={r.id}>{r.title}</option>)}
              </select>
            </div>
            {done && <div className="g-note g-note--ok" role="status"><span>{done}</span></div>}
            <Fail text={error} />
            <span className="acts">
              <button className="g-btn g-btn--primary" type="submit" disabled={busy || !restaurantId || (!editing && (!form.login.trim() || !form.password))}>
                {editing ? 'Сохранить' : 'Завести сотрудника'}
              </button>
              {editing && <button className="g-btn" type="button" disabled={busy} onClick={() => startEdit(null)}>Отмена</button>}
            </span>
          </form>
        </section>
      </div>
    </>
  );
}
