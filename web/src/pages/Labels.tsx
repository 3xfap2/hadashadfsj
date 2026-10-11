import qrcode from 'qrcode-generator';
import { Link, useSearchParams } from 'react-router-dom';
import { useApi, type BatchRow } from '../api';
import { dayMonthShort, qty, weekday } from '../format';
import { isNative, PUBLIC_ORIGIN } from '../native';

const dateTime = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });

/** QR-код как SVG: печатается чётко в любом размере. */
export function Qr({ text, label = 'QR-код партии' }: { text: string; label?: string }) {
  const code = qrcode(0, 'M');
  code.addData(text);
  code.make();
  const size = code.getModuleCount();
  let path = '';
  for (let row = 0; row < size; row++) for (let col = 0; col < size; col++) if (code.isDark(row, col)) path += `M${col} ${row}h1v1h-1z`;
  return (
    <svg className="label__qr" viewBox={`-2 -2 ${size + 4} ${size + 4}`} shapeRendering="crispEdges" role="img" aria-label={label}>
      <path d={path} />
    </svg>
  );
}

/**
 * Этикетки партий для печати. На бумаге дата всегда полная: «завтра» на наклейке через день станет неправдой.
 * QR-код ведёт на эту партию в «Складе» — оттуда её можно списать.
 */
export function Labels() {
  const [params] = useSearchParams();
  const batches = useApi<BatchRow[]>('/stock/batches', 300_000);
  const supply = params.get('supply');
  const batch = params.get('batch');

  if (batches.error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить партии</h3><span>{batches.error}</span></div>;
  if (!batches.data) return <p className="muted">Готовим этикетки…</p>;

  const shown = batches.data.filter((b) => b.remaining > 0 && (batch ? b.id === batch : supply ? b.supplyNumber === supply : false));

  return (
    <>
      <div className="g-title labels-head">
        <div className="buy-head">
          <h1>Этикетки</h1>
          <p className="lead-text">
            {supply ? `Поставка ${supply}: ` : ''}по одной на партию. Наклейте на тару; по QR-коду партия откроется на телефоне.
          </p>
        </div>
        <div className="end stock-tools">
          <Link className="g-btn" to="/app/stock">К складу</Link>
          {/* Печать из приложения не работает: этикетки печатают с компьютера. */}
          {!isNative && <button className="g-btn g-btn--primary" type="button" disabled={shown.length === 0} onClick={() => window.print()}>Печать</button>}
        </div>
      </div>

      {shown.length === 0 ? (
        <p className="g-empty"><strong>Партий для печати нет</strong>Этикетки печатаются для партий, у которых есть остаток.</p>
      ) : (
        <ul className="labels">
          {shown.map((b) => (
            <li key={b.id} className="label">
              <div className="label__text">
                <strong className="label__name">{b.productName}</strong>
                <span className="label__until">
                  <span className="label__day">{weekday(b.expiresAt)}</span>
                  <span className="num">до {dateTime.format(new Date(b.expiresAt)).replace(',', '')}</span>
                </span>
                <span className="num">{qty(b.quantity, b.unit)} · {b.supplyNumber} · принято {dayMonthShort(b.receivedAt)}</span>
              </div>
              <Qr text={`${PUBLIC_ORIGIN}/app/stock?batch=${b.id}`} />
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
