import { useState, type FormEvent } from 'react';
import { login, takeExpired, type LoginError } from '../api';

export function Login() {
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [tried, setTried] = useState(false);
  const [error, setError] = useState<LoginError | null>(
    () => (takeExpired() ? { title: 'Сессия закончилась', text: 'Войдите снова. Так бывает, если сменили пароль или прошло больше 12 часов.' } : null),
  );
  const [busy, setBusy] = useState(false);
  // Бесплатный сервер после простоя отвечает не сразу: человек должен понимать, что вход идёт.
  const [slow, setSlow] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    if (!name.trim() || !password) return;
    setBusy(true);
    const waiting = setTimeout(() => setSlow(true), 4000);
    try {
      setError(await login(name.trim(), password));
    } catch {
      setError({ title: 'Сервер не отвечает', text: 'Войти сейчас нельзя. Попробуйте ещё раз через минуту.' });
    } finally {
      clearTimeout(waiting);
      setSlow(false);
      setBusy(false);
    }
  };

  const loginMissing = tried && !name.trim();
  const passwordMissing = tried && !password;

  return (
    <div className="g gate">
      <form className="login" onSubmit={submit} noValidate>
        <span className="g-brand">Остаток <span className="shell__crm">владелец</span></span>
        <div>
          <h1>Кабинет владельца</h1>
          <p className="muted">Все ваши рестораны на одном экране: выручка, списания, сотрудники и поставщики. Вход — тем же логином и паролем, что и в кабинет ресторана.</p>
        </div>

        {error && (
          <div className="g-note g-note--err" role="alert">
            <h3>{error.title}</h3>
            <span>{error.text}</span>
          </div>
        )}

        <div className={`g-field${loginMissing ? ' g-field--error' : ''}`}>
          <label htmlFor="login">Логин</label>
          <input className="g-input" id="login" autoComplete="username" autoCapitalize="none" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={loginMissing} />
          {loginMissing && <span className="g-error-text">Введите логин</span>}
        </div>

        <div className={`g-field${passwordMissing ? ' g-field--error' : ''}`}>
          <label htmlFor="password">Пароль</label>
          <input className="g-input" id="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} aria-invalid={passwordMissing} />
          {passwordMissing && <span className="g-error-text">Введите пароль</span>}
        </div>

        <button className="g-btn g-btn--primary g-btn--block" disabled={busy}>{busy ? 'Входим…' : 'Войти'}</button>
        {/* Область объявлений есть всегда и только меняет текст: иначе экранный диктор её не прочтёт. */}
        <p className="muted small" role="status">{slow ? 'Сервер просыпается после простоя — это занимает до минуты.' : ''}</p>
        <p className="muted small">Кабинет ресторана — <a className="hd" href="/login">на основном сайте</a>.</p>
      </form>
    </div>
  );
}
