import { useState, type FormEvent } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { login, type LoginError } from '../api';

// Открытая учётная запись демо-ресторана: по ней жюри и гости смотрят систему без регистрации.
const DEMO = { login: 'demo', password: 'demo2026' };

export function Login() {
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [showHint, setShowHint] = useState(false);
  const [tried, setTried] = useState(false);
  // С лендинга сюда приводит неудачный вход в демо — причина приходит вместе с переходом.
  const [error, setError] = useState<LoginError | null>((useLocation().state as { error?: LoginError } | null)?.error ?? null);
  const [busy, setBusy] = useState(false);

  const enter = async (l: string, p: string) => {
    setBusy(true);
    setError(await login(l, p));
    setBusy(false);
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    if (name && password) void enter(name, password);
  };

  const loginMissing = tried && !name;
  const passwordMissing = tried && !password;

  return (
    <div className="g login">
      <div className="login__photo-col">
        <div className="login__photo">
          <img src="/photos/pass.jpg" alt="" />
        </div>
      </div>

      <main className="login__main">
        <Link className="g-brand" to="/">Остаток</Link>
        <form className="login__form" onSubmit={submit} noValidate>
          <div>
            <h1>Вход в кабинет</h1>
            <p className="muted">Ресторан, склад и стоп-лист — в одном месте.</p>
          </div>

          {error && (
            <div className="g-note g-note--err" role="alert">
              <h3>{error.title}</h3>
              <span>{error.text}</span>
            </div>
          )}

          <div className={`g-field${loginMissing ? ' g-field--error' : ''}`}>
            <label htmlFor="login">Логин</label>
            <input
              className="g-input"
              id="login"
              autoComplete="username"
              autoCapitalize="none"
              value={name}
              onChange={(e) => setName(e.target.value)}
              aria-invalid={loginMissing}
              aria-describedby={loginMissing ? 'login-error' : undefined}
            />
            {loginMissing && <span className="g-error-text" id="login-error">Введите логин</span>}
          </div>

          <div className={`g-field${passwordMissing ? ' g-field--error' : ''}`}>
            <div className="login__label-row">
              <label htmlFor="password">Пароль</label>
              <button type="button" aria-expanded={showHint} aria-controls="password-hint" onClick={() => setShowHint((v) => !v)}>
                Забыли пароль?
              </button>
            </div>
            <div className="g-unit">
              <input
                className="g-input"
                id="password"
                type={showPassword ? 'text' : 'password'}
                autoComplete="current-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                aria-invalid={passwordMissing}
                aria-describedby={passwordMissing ? 'password-error' : undefined}
              />
              <button className="login__toggle" type="button" onClick={() => setShowPassword((v) => !v)}>
                {showPassword ? 'Скрыть' : 'Показать'}
              </button>
            </div>
            {passwordMissing && <span className="g-error-text" id="password-error">Введите пароль</span>}
            {showHint && (
              <span className="g-hint" id="password-hint" role="status">
                Пароль сбрасывает администратор вашего ресторана — обратитесь к нему.
              </span>
            )}
          </div>

          <button className="g-btn g-btn--primary g-btn--block" disabled={busy}>
            {busy ? 'Входим…' : 'Войти'}
          </button>
          <div className="login__or"><span />или<span /></div>
          <button type="button" className="g-btn g-btn--block" disabled={busy} onClick={() => enter(DEMO.login, DEMO.password)}>
            Открыть демо-ресторан
          </button>
          <p className="muted">Демо — ресторан на 60 мест с партиями, сроками годности и стоп-листом.</p>
        </form>
        <p className="muted">Нет кабинета? <Link className="hd" to="/">Узнать, как подключиться</Link></p>
      </main>
    </div>
  );
}
