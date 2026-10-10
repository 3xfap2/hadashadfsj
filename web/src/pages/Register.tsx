import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { register } from '../api';

/** Создание нового ресторана: своё рабочее пространство и первый пользователь — управляющий. */
export function Register() {
  const [form, setForm] = useState({ restaurant: '', login: '', password: '' });
  const [tried, setTried] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const login = form.login.trim().toLowerCase();
  const bad = {
    restaurant: tried && !form.restaurant.trim(),
    login: tried && !/^[a-z0-9._-]{3,40}$/.test(login),
    password: tried && form.password.length < 8,
  };
  const field = (invalid: boolean) => `g-field${invalid ? ' g-field--error' : ''}`;
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [key]: e.target.value }));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    if (!form.restaurant.trim() || !/^[a-z0-9._-]{3,40}$/.test(login) || form.password.length < 8) return;
    setBusy(true);
    // После успешной регистрации вход выполнен, и страница сама переходит в кабинет.
    setError((await register(form.restaurant.trim(), login, form.password)) ?? '');
    setBusy(false);
  };

  return (
    <div className="g login">
      <div className="login__photo-col">
        <div className="login__photo">
          <img src="/photos/hall.jpg" alt="" />
        </div>
      </div>

      <main className="login__main">
        <Link className="g-brand" to="/">Остаток</Link>
        <form className="login__form" onSubmit={submit} noValidate>
          <div>
            <h1>Новый ресторан</h1>
            <p className="muted">У ресторана свой склад, меню и сотрудники. Вы станете управляющим и заведёте учётные записи остальным.</p>
          </div>

          {error && <div className="g-note g-note--err" role="alert"><h3>Ресторан не создан</h3><span>{error}</span></div>}

          <div className={field(bad.restaurant)}>
            <label htmlFor="reg-name">Название ресторана</label>
            <input className="g-input" id="reg-name" autoComplete="organization" value={form.restaurant} onChange={set('restaurant')} aria-invalid={bad.restaurant} />
            {bad.restaurant && <span className="g-error-text">Укажите название</span>}
          </div>
          <div className={field(bad.login)}>
            <label htmlFor="reg-login">Логин управляющего</label>
            <input className="g-input" id="reg-login" autoComplete="username" autoCapitalize="none" value={form.login} onChange={set('login')} aria-invalid={bad.login} />
            {bad.login
              ? <span className="g-error-text">От 3 до 40 символов: латинские буквы, цифры, точка, дефис</span>
              : <span className="g-hint">Латинские буквы и цифры, например trattoria.admin</span>}
          </div>
          <div className={field(bad.password)}>
            <label htmlFor="reg-password">Пароль</label>
            <input className="g-input" id="reg-password" type="password" autoComplete="new-password" value={form.password} onChange={set('password')} aria-invalid={bad.password} />
            {bad.password ? <span className="g-error-text">Не короче 8 символов</span> : <span className="g-hint">Не короче 8 символов</span>}
          </div>

          <button className="g-btn g-btn--primary g-btn--block" disabled={busy}>{busy ? 'Создаём…' : 'Создать ресторан'}</button>
        </form>
        <p className="muted">Уже есть кабинет? <Link className="hd" to="/login">Войти</Link></p>
      </main>
    </div>
  );
}
