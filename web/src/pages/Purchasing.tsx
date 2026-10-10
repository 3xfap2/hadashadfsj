import { useState } from 'react';
import { useApi, type PurchaseRow, type StockRow } from '../api';
import { money, plural, qty } from '../format';

const NO_SUPPLIER = 'Поставщик не указан';

export function Purchasing() {
  const items = useApi<PurchaseRow[]>('/purchase-suggestions', 60_000);
  const stock = useApi<StockRow[]>('/stock', 60_000);
  const [off, setOff] = useState<Record<string, boolean>>({});
  const [copied, setCopied] = useState(false);

  const error = items.error ?? stock.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить закупки</h3><span>{error}</span></div>;
  if (!items.data || !stock.data) return <p className="muted">Считаем закупки…</p>;

  const picked = items.data.filter((i) => !off[i.productId]);
  const groups = new Map<string, PurchaseRow[]>();
  for (const item of picked) groups.set(item.supplier ?? NO_SUPPLIER, [...(groups.get(item.supplier ?? NO_SUPPLIER) ?? []), item]);
  const total = picked.reduce((sum, i) => sum + i.sum, 0);
  // Продукты, которые есть на складе и в список закупок не попали.
  const enough = stock.data.filter((s) => s.fresh > 0 && !items.data!.some((i) => i.productId === s.productId)).map((s) => s.productName.toLowerCase());

  const text = () =>
    [...groups]
      .map(([supplier, rows]) => `${supplier}\n${rows.map((r) => `${r.productName} — ${qty(r.quantity, r.unit)}`).join('\n')}`)
      .join('\n\n');

  const copy = async () => {
    await navigator.clipboard.writeText(text());
    setCopied(true);
    setTimeout(() => setCopied(false), 4000);
  };

  const download = () => {
    const url = URL.createObjectURL(new Blob(['﻿' + text()], { type: 'text/plain;charset=utf-8' }));
    Object.assign(document.createElement('a'), { href: url, download: 'zayavka.txt' }).click();
    URL.revokeObjectURL(url);
  };

  return (
    <>
      <div className="buy-head">
        <h1>Закупки</h1>
        <p className="lead-text">Что пора заказать и почему. Расход считается по продажам за последние 7 дней.</p>
      </div>

      <div className="sup-row">
        <section className="sup-main g-panel buy-panel" aria-labelledby="buy-h">
          <div className="g-panel__head"><h2 id="buy-h">Пора заказать</h2><span className="end">сначала то, что кончится раньше</span></div>
          {items.data.length === 0 ? (
            <p className="g-empty"><strong>Заказывать нечего</strong>По остаткам и расходу заказывать сейчас ничего не нужно.</p>
          ) : (
            <ul>
              {items.data.map((i) => {
                const urgent = (i.hoursCovered ?? 0) < 24;
                return (
                  <li key={i.productId} className="buy">
                    <input type="checkbox" id={`b-${i.productId}`} checked={!off[i.productId]} onChange={() => setOff((o) => ({ ...o, [i.productId]: !o[i.productId] }))} aria-label={`Добавить в заявку: ${i.productName}`} />
                    <span className="g-photo" role="img" aria-label={`Место для фото: ${i.productName}`}><span aria-hidden="true">{initials(i.productName)}</span></span>
                    <div className="buy__text">
                      <div className="buy__name">
                        <label className="g-name" htmlFor={`b-${i.productId}`}>{i.productName}</label>
                        <span className={urgent ? 'g-tag g-tag--red' : 'g-tag g-tag--amber'}>{urgent ? 'Заказать сегодня' : (i.hoursCovered ?? 0) < 48 ? 'До завтра' : 'Скоро'}</span>
                      </div>
                      <p>{i.reason}</p>
                      <span className="muted small">Поставщик: {i.supplier ?? 'не указан'}</span>
                    </div>
                    <div className="buy__end">
                      <span className="num hd">{qty(i.quantity, i.unit)}</span>
                      <span className="muted num">{money(i.sum)}</span>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
          {enough.length > 0 && <div className="g-panel__foot"><span className="muted">Есть на складе и в заявку не попали: {enough.join(', ')}</span></div>}
        </section>

        <aside className="sup-side">
          <section className="g-panel req-panel" aria-labelledby="req-h">
            <h2 id="req-h">Заявка на завтра</h2>
            <span className="muted">{plural(picked.length, 'позиция', 'позиции', 'позиций')}</span>
            {[...groups].map(([supplier, rows]) => (
              <div className="grp" key={supplier}>
                <strong className="hd">{supplier}</strong>
                <span className="grp__lines">{rows.map((r) => `${r.productName} — ${qty(r.quantity, r.unit)}`).join('\n')}</span>
                <span className="num hd grp__sum">{money(rows.reduce((sum, r) => sum + r.sum, 0))}</span>
              </div>
            ))}
            <div className="req-total"><span>Итого</span><span className="num">{money(total)}</span></div>
            <button className="g-btn g-btn--primary g-btn--block" type="button" disabled={picked.length === 0} onClick={copy}>
              {copied ? 'Заявка скопирована' : 'Скопировать заявку'}
            </button>
            <button className="g-btn g-btn--block" type="button" disabled={picked.length === 0} onClick={download}>Скачать заявку</button>
            <span className="sr" role="status">{copied ? 'Текст заявки скопирован' : ''}</span>
          </section>
        </aside>
      </div>
    </>
  );
}

function initials(name: string): string {
  const words = name.split(' ');
  return (words.length > 1 ? words[0][0] + words[1][0] : name.slice(0, 2)).toUpperCase();
}
