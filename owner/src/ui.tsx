import { useState } from 'react';

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

/** Период сводки и рейтинга: сегодня, неделя, месяц. */
export const PERIODS = [
  { days: 1, title: 'Сегодня' },
  { days: 7, title: '7 дней' },
  { days: 30, title: '30 дней' },
];

export function Period({ days, onChange }: { days: number; onChange: (days: number) => void }) {
  return (
    <div className="g-seg" role="group" aria-label="Период">
      {PERIODS.map((p) => (
        <button key={p.days} type="button" aria-pressed={p.days === days} onClick={() => onChange(p.days)}>{p.title}</button>
      ))}
    </div>
  );
}
