import type { BatchRow } from './api';
import { duration, until, weekday } from './format';

/** Меньше этого срока партия считается срочной: красная метка вместо обычного текста. */
export const URGENT_HOURS = 6;

export function Photo({ url, name }: { url: string | null; name: string }) {
  const initials = name
    .split(/\s+/)
    .filter((w) => /^[А-ЯЁA-Z«]/.test(w))
    .slice(0, 2)
    .map((w) => w.replace('«', '')[0])
    .join('');
  return (
    <span className="g-photo" role="img" aria-label={`Фото блюда: ${name}`}>
      {url ? <img src={url} alt="" loading="lazy" /> : <span aria-hidden="true">{initials}</span>}
    </span>
  );
}

/** Этикетка партии: день недели, «годен до», остаток срока. */
export function BatchLabel({ batch }: { batch: BatchRow }) {
  const expired = batch.status === 'Expired';
  const kind = expired || batch.hoursLeft < URGENT_HOURS ? 'red' : batch.status === 'ExpiringSoon' ? 'amber' : '';
  const left = expired ? 'просрочено' : duration(batch.hoursLeft);
  return (
    <span
      className={`g-lbl${kind ? ` g-lbl--${kind}` : ''}`}
      role="img"
      aria-label={`${expired ? 'Просрочено' : 'Истекает'}: годен ${until(batch.expiresAt)}, ${left}`}
    >
      <span className="g-lbl__d" aria-hidden="true">{weekday(batch.expiresAt)}</span>
      <span className="g-lbl__b" aria-hidden="true">
        <span>{until(batch.expiresAt)}</span>
        <span className="g-lbl__left">{left}</span>
      </span>
    </span>
  );
}

export const PlusIcon = () => (
  <svg viewBox="0 0 24 24" aria-hidden="true">
    <path d="M12 5v14M5 12h14" />
  </svg>
);
