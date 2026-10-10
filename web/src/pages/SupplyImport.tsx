import { useState, type ChangeEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { post, postFile, type ImportRow, type SupplyRow } from '../api';
import { money, plural, qty, shelfLife } from '../format';
import { saveDraft } from '../supplyDraft';

const TEMPLATE = 'продукт;количество;единица;срок хранения;цена\r\nЛосось охлаждённый;3;кг;72 ч;5550\r\nСливки 33%;3;л;5 дней;1560\r\n';
const CHIPS = { Ok: ['muted', 'Готово', ''], Warning: ['g-tag g-tag--amber', 'Предупреждение', 'warn'], Error: ['g-tag g-tag--red', 'Ошибка', 'bad'] } as const;

type Result = { kind: 'ok' | 'err'; title: string; text: string } | null;

export function SupplyImport() {
  const navigate = useNavigate();
  const [fileName, setFileName] = useState('');
  const [rows, setRows] = useState<ImportRow[] | null>(null);
  const [number, setNumber] = useState('');
  const [onlyBad, setOnlyBad] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);

  const choose = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setBusy(true);
    setResult(null);
    setRows(null);
    setFileName(file.name);
    // Номер поставки часто стоит в имени файла: postavka_P-0422.xlsx → П-0422.
    const inName = /[PП]-?(\d{2,})/i.exec(file.name);
    setNumber(inName ? `П-${inName[1]}` : '');
    try {
      const response = await postFile('/supplies/import/preview', file);
      if (response.ok) setRows(await response.json());
      else setResult({ kind: 'err', title: 'Файл не прочитан', text: (await response.json().catch(() => ({}))).detail ?? `Сервер ответил ошибкой ${response.status}.` });
    } catch (err) {
      setResult({ kind: 'err', title: 'Файл не прочитан', text: (err as Error).message });
    }
    setBusy(false);
  };

  const downloadTemplate = () => {
    // Метка в начале файла нужна, чтобы Excel открыл русские буквы правильно.
    const url = URL.createObjectURL(new Blob(['﻿' + TEMPLATE], { type: 'text/csv;charset=utf-8' }));
    const link = Object.assign(document.createElement('a'), { href: url, download: 'shablon-postavki.csv' });
    link.click();
    URL.revokeObjectURL(url);
  };

  const good = rows?.filter((r) => r.status !== 'Error') ?? [];
  const bad = rows?.filter((r) => r.status === 'Error') ?? [];
  const warned = good.filter((r) => r.status === 'Warning').length;
  const step = result?.kind === 'ok' ? 3 : rows ? 2 : 1;

  const upload = async () => {
    if (!number.trim()) return setResult({ kind: 'err', title: 'Укажите номер поставки', text: 'Без номера поставку не принять.' });
    setBusy(true);
    try {
      const response = await post('/supplies', {
        number: number.trim(),
        items: good.map((r) => ({ productId: r.productId, quantity: r.quantity, shelfLifeHours: r.shelfLifeHours })),
      });
      if (response.status === 201) {
        const created: SupplyRow = await response.json();
        setResult({ kind: 'ok', title: `Поставка ${created.number} принята`, text: `На склад добавлено: ${plural(created.items.length, 'партия', 'партии', 'партий')} на ${money(created.total)}.` });
      } else if (response.status === 409) {
        setResult({ kind: 'err', title: 'Поставка не принята', text: 'Поставка с таким номером уже есть. Укажите другой номер.' });
      } else {
        const problem = await response.json().catch(() => ({}));
        const messages = Object.values((problem.errors ?? {}) as Record<string, string[]>).flat();
        setResult({ kind: 'err', title: 'Поставка не принята', text: messages.join(' ') || `Сервер ответил ошибкой ${response.status}.` });
      }
    } catch (err) {
      setResult({ kind: 'err', title: 'Поставка не принята', text: (err as Error).message });
    }
    setBusy(false);
  };

  // Строки без ошибок переносятся в форму поставки как черновик — там их можно поправить руками.
  const editByHand = () => {
    saveDraft({ number, date: '', supplier: '', lines: good.map((r) => ({ productId: r.productId!, quantity: String(r.quantity).replace('.', ','), life: String(r.shelfLifeHours) })) });
    navigate('/app/stock/supplies');
  };

  return (
    <>
      <div className="g-title imp-title">
        <div className="stock-head">
          <h1>Загрузка поставки</h1>
          <Link className="muted" to="/app/stock/supplies">← К поставкам</Link>
        </div>
        <div className="end">
          <ol className="imp-steps" aria-label="Шаги загрузки">
            {['Файл', 'Проверка строк', 'Подтверждение'].map((title, i) => (
              <li key={title} className={`step${i + 1 < step ? ' done' : i + 1 === step ? ' now' : ''}`} aria-current={i + 1 === step ? 'step' : undefined}>
                <b aria-label={i + 1 < step ? 'готово' : undefined}>
                  {i + 1 < step ? <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12 5 5 9-10" /></svg> : i + 1}
                </b>
                {title}
              </li>
            ))}
          </ol>
        </div>
      </div>

      <div className="drop">
        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h8l4 4v14H6zM14 3v4h4M9 13h6M9 17h6" /></svg>
        <div className="drop__text">
          <strong className="hd">{fileName || 'Файл не выбран'}</strong>
          <span className="muted">{busy && !rows ? 'Читаем файл…' : rows ? plural(rows.length, 'строка', 'строки', 'строк') : 'Выберите файл поставки от поставщика'}</span>
        </div>
        <label className="g-btn" htmlFor="file">{fileName ? 'Выбрать другой файл' : 'Выбрать файл'}</label>
        <input id="file" type="file" accept=".xlsx,.csv" className="sr" onChange={choose} />
        <span className="muted small drop__hint">
          Подходит Excel или CSV с колонками: продукт, количество, единица, срок хранения, цена.{' '}
          <button type="button" className="hd drop__link" onClick={downloadTemplate}>Скачать шаблон</button>
        </span>
      </div>

      {result && (
        <div className={`g-note g-note--${result.kind}`} role={result.kind === 'err' ? 'alert' : 'status'}>
          <h3>{result.title}</h3>
          <span>{result.text}</span>
          {result.kind === 'ok' && <Link className="hd" to="/app/stock">Открыть остатки</Link>}
        </div>
      )}

      {rows && result?.kind !== 'ok' && (
        <div className="sup-row">
          <section className="sup-main g-panel" aria-labelledby="pv-h">
            <div className="g-panel__head imp-head">
              <h2 id="pv-h">Предпросмотр</h2>
              <div className="g-seg" role="group" aria-label="Какие строки показать">
                <button type="button" aria-pressed={!onlyBad} onClick={() => setOnlyBad(false)}>Все <span className="n">{rows.length}</span></button>
                <button type="button" aria-pressed={onlyBad} onClick={() => setOnlyBad(true)}>С ошибками <span className="n">{bad.length}</span></button>
              </div>
            </div>
            <div className="g-table-wrap">
              <table className="g-table imp-table">
                <thead>
                  <tr><th className="r">Строка</th><th>Продукт в файле</th><th className="r">Количество</th><th className="r">Срок</th><th className="r">Цена</th><th>Проверка</th></tr>
                </thead>
                <tbody>
                  {(onlyBad ? bad : rows).map((r) => (
                    <tr key={r.line} className={CHIPS[r.status][2]}>
                      <td className="num r muted">{r.line}</td>
                      <td className="g-name">{r.productText || '—'}</td>
                      <td className="r num hd">{r.quantity !== null && r.unit ? qty(r.quantity, r.unit) : '—'}</td>
                      <td className="r num">{r.shelfLifeHours !== null ? shelfLife(r.shelfLifeHours) : '—'}</td>
                      <td className="r num hd imp-price">{r.price !== null ? money(r.price) : '—'}</td>
                      <td className="imp-check">
                        <span className={CHIPS[r.status][0]}>{CHIPS[r.status][1]}</span>
                        {r.message && <div className="small imp-msg">{r.message}</div>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <aside className="sup-side imp-side">
            <section className="g-panel">
              <div className="g-panel__head"><h2 className="imp-sum-title">Итог проверки</h2></div>
              <div className="imp-sum">
                <div><span>Готовы к загрузке</span><strong className="num r hd">{good.length - warned}</strong></div>
                <div><span>С предупреждением</span><strong className="num r hd">{warned}</strong></div>
                <div className={bad.length > 0 ? 'imp-sum__bad' : ''}><span>С ошибками</span><span className="num r">{bad.length}</span></div>
                <div className="imp-sum__total"><span>Сумма к загрузке</span><strong className="num r hd">{money(good.reduce((s, r) => s + (r.price ?? 0), 0))}</strong></div>
                <div className="g-field">
                  <label htmlFor="imp-no">Номер поставки</label>
                  <input className="g-input num" id="imp-no" value={number} onChange={(e) => setNumber(e.target.value)} />
                </div>
                {bad.length > 0 && (
                  <div className="g-note g-note--err">
                    <h3>{plural(bad.length, 'строка', 'строки', 'строк')} не {bad.length === 1 ? 'загрузится' : 'загрузятся'}</h3>
                    <span>
                      Исправьте их в файле и загрузите снова, или пропустите — {good.length > 0 ? `остальные ${plural(good.length, 'строка попадёт', 'строки попадут', 'строк попадут')} на склад` : 'но тогда загружать будет нечего'}.
                    </span>
                  </div>
                )}
              </div>
              <div className="g-panel__foot imp-foot">
                <button className="g-btn g-btn--primary g-btn--block" type="button" disabled={busy || good.length === 0} onClick={upload}>
                  {busy ? 'Загружаем…' : `Загрузить ${plural(good.length, 'строку', 'строки', 'строк')}${bad.length > 0 ? `, пропустить ${bad.length}` : ''}`}
                </button>
                <button className="g-btn g-btn--block" type="button" disabled={good.length === 0} onClick={editByHand}>Исправить вручную в форме</button>
              </div>
            </section>
          </aside>
        </div>
      )}
    </>
  );
}
