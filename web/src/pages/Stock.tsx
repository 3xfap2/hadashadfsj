import { useEffect, useRef, useState } from 'react';
import { Link, NavLink, useSearchParams } from 'react-router-dom';
import { post, useApi, type BatchRow, type ProductRow, type StockRow, type StopListRow } from '../api';
import { dayMonthShort, dueLong, leftPrecise, money, qty, shelfLife, unitName } from '../format';
import { isNative } from '../native';
import { URGENT_HOURS, useCan } from '../ui';

type Kind = 'hot' | 'soon' | 'ok' | 'expired' | 'low';

const KINDS: Record<Kind, { chip: string; word: string; color: string }> = {
  hot: { chip: 'g-tag g-tag--red', word: 'Срочно', color: 'var(--red)' },
  soon: { chip: 'g-tag g-tag--amber', word: 'Скоро', color: 'var(--amber)' },
  ok: { chip: 'plain', word: 'В порядке', color: 'var(--h)' },
  expired: { chip: 'g-tag g-tag--red', word: 'Просрочено', color: 'var(--red)' },
  low: { chip: 'g-tag g-tag--amber', word: 'Мало', color: 'var(--amber)' },
};
const FILTERS: { id: Kind | 'all'; label: string }[] = [
  { id: 'all', label: 'Все' },
  { id: 'hot', label: 'Срочно' },
  { id: 'soon', label: 'Скоро' },
  { id: 'expired', label: 'Просрочено' },
  { id: 'low', label: 'Мало' },
];
const ORDER: Kind[] = ['expired', 'hot', 'soon', 'low', 'ok'];

const batchKind = (b: BatchRow): Kind =>
  b.status === 'Expired' ? 'expired' : b.hoursLeft < URGENT_HOURS ? 'hot' : b.status === 'ExpiringSoon' ? 'soon' : 'ok';

export function StockTabs() {
  // Заготовки делает кухня: вкладка видна тем, кто работает с меню.
  const kitchen = useCan('kitchen');
  const stock = useCan('stock');
  // Повару без доступа к складу остаются одни «Заготовки»: переключать нечего.
  if (!stock) return null;
  return (
    <nav className="g-seg" aria-label="Раздел склада">
      <NavLink to="/app/stock" end>Остатки</NavLink>
      <NavLink to="/app/stock/supplies">Поставки</NavLink>
      {kitchen && <NavLink to="/app/stock/preps">Заготовки</NavLink>}
      <NavLink to="/app/stock/inventory">Инвентаризация</NavLink>
      <NavLink to="/app/stock/journal">Журнал</NavLink>
    </nav>
  );
}

export function Stock() {
  const stock = useApi<StockRow[]>('/stock');
  const batches = useApi<BatchRow[]>('/stock/batches');
  const products = useApi<ProductRow[]>('/products', 300_000);
  const stopList = useApi<StopListRow[]>('/stop-list');
  const [filter, setFilter] = useState<Kind | 'all'>('all');
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState<Record<string, boolean>>({});
  // Партия, для которой сейчас спрашивается причина списания.
  const [asking, setAsking] = useState<string | null>(null);
  // Партия, которую вскрывают: сколько из неё вскрыто и что ответил сервер.
  const [opening, setOpening] = useState<{ id: string; quantity: string; error: string } | null>(null);
  const [busy, setBusy] = useState(false);
  // Партия, открытая по QR-коду с этикетки: её продукт раскрыт, строка выделена.
  const [params, setParams] = useSearchParams();
  const target = params.get('batch');
  const [scanNote, setScanNote] = useState('');

  // В приложении этикетку можно отсканировать камерой: откроется та же партия, что и по ссылке из QR-кода.
  const scan = async () => {
    setScanNote('');
    try {
      const { CapacitorBarcodeScanner, CapacitorBarcodeScannerTypeHint } = await import('@capacitor/barcode-scanner');
      const result = await CapacitorBarcodeScanner.scanBarcode({
        hint: CapacitorBarcodeScannerTypeHint.QR_CODE,
        scanInstructions: 'Наведите камеру на QR-код этикетки',
      });
      const id = batchFromLabel(result.ScanResult);
      if (id && batches.data?.some((b) => b.id === id)) setParams({ batch: id });
      else setScanNote('Партия не найдена: это не этикетка вашего склада, либо партия уже израсходована или списана.');
    } catch {
      // Сканер закрыли, не отсканировав, или камера недоступна — остаёмся на складе.
    }
  };
  const targetRow = useRef<HTMLLIElement>(null);
  const loaded = Boolean(batches.data);
  useEffect(() => {
    if (loaded) targetRow.current?.scrollIntoView({ block: 'center' });
  }, [loaded, target]);

  const openBatch = async () => {
    if (!opening) return;
    setBusy(true);
    try {
      const response = await post(`/stock/batches/${opening.id}/open`, { quantity: Number(opening.quantity.replace(',', '.')) });
      if (response.ok) {
        setOpening(null);
        await Promise.all([stock.reload(), batches.reload(), stopList.reload()]);
      } else {
        const problem = await response.json().catch(() => ({}));
        const messages = Object.values((problem.errors ?? {}) as Record<string, string[]>).flat();
        setOpening({ ...opening, error: messages.join(' ') || problem.detail || `Сервер ответил ошибкой ${response.status}.` });
      }
    } catch (e) {
      setOpening({ ...opening, error: (e as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const writeOff = async (batch: BatchRow, reason?: 'Spoiled' | 'Defective') => {
    setBusy(true);
    try {
      await post(`/stock/batches/${batch.id}/write-off`, reason ? { reason } : undefined);
      await Promise.all([stock.reload(), batches.reload(), stopList.reload()]);
      setAsking(null);
    } catch {
      // Связь оборвалась: партия осталась в списке, списание можно повторить.
    } finally {
      setBusy(false);
    }
  };

  const error = stock.error ?? batches.error ?? products.error ?? stopList.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить склад</h3><span>{error}</span></div>;
  if (!stock.data || !batches.data || !products.data || !stopList.data) return <p className="muted">Загружаем склад…</p>;

  // Продукт могли завести, пока экран открыт: его строка появится, когда обновится справочник.
  const rows = stock.data
    .filter((s) => products.data!.some((p) => p.id === s.productId))
    .map((s) => {
      const product = products.data!.find((p) => p.id === s.productId)!;
      const own = batches.data!.filter((b) => b.productId === s.productId);
      const fresh = own.filter((b) => b.status !== 'Expired');
      const expiredValue = own.filter((b) => b.status === 'Expired').reduce((sum, b) => sum + (b.value ?? 0), 0);
      // Блюда, которые стоят или заканчиваются из-за этого продукта.
      const blocked = stopList.data!.filter((d) => d.status !== 'Available' && d.limitingProductId === s.productId);
      const stopped = blocked.filter((d) => d.status === 'Stop');

      let kind: Kind;
      let next: string;
      if (fresh.length === 0 && own.length > 0) {
        kind = 'expired';
        next = `годной нет · ждёт списания ${money(expiredValue)}`;
      } else if (fresh.length === 0) {
        kind = 'low';
        next = stopped.length > 0 ? `нет на складе: ${stopped[0].dishName.toLowerCase()} в стопе` : 'нет на складе';
      } else if (batchKind(fresh[0]) !== 'ok') {
        kind = batchKind(fresh[0]);
        next = fresh.length > 1
          ? `${qty(fresh[0].remaining, s.unit)} — ${dueLong(fresh[0].expiresAt)}`
          : `через ${leftPrecise(fresh[0].hoursLeft)}`;
      } else if (blocked.length > 0) {
        kind = 'low';
        next = `мало: ${blocked[0].dishName.toLowerCase()} ${blocked[0].status === 'Stop' ? 'в стопе' : 'заканчивается'}`;
      } else {
        kind = 'ok';
        next = `до ${dayMonthShort(fresh[0].expiresAt)}`;
      }
      return { ...s, product, batches: own, kind, next };
    })
    .sort((a, b) => ORDER.indexOf(a.kind) - ORDER.indexOf(b.kind) || a.productName.localeCompare(b.productName, 'ru'));

  const count = (id: Kind | 'all') => (id === 'all' ? rows.length : rows.filter((r) => r.kind === id).length);
  const shown = rows.filter((r) => (filter === 'all' || r.kind === filter) && r.productName.toLowerCase().includes(search.trim().toLowerCase()));

  return (
    <>
      <div className="g-title">
        <div className="stock-head">
          <h1>Склад</h1>
          <StockTabs />
        </div>
        <div className="end stock-tools">
          <label className="sr" htmlFor="product-search">Найти продукт</label>
          <input className="g-input" id="product-search" type="search" placeholder="Найти продукт" value={search} onChange={(e) => setSearch(e.target.value)} />
          {isNative && <button className="g-btn" type="button" onClick={scan}>Сканировать этикетку</button>}
          <Link className="g-btn g-btn--primary" to="/app/stock/supplies">Принять поставку</Link>
        </div>
      </div>
      {scanNote && <div className="g-note g-note--err" role="alert"><span>{scanNote}</span></div>}

      <div className="g-seg stock-filters" role="group" aria-label="Срочность">
        {FILTERS.map((f) => (
          <button key={f.id} type="button" aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>
            {f.label} <span className="n num">{count(f.id)}</span>
          </button>
        ))}
      </div>

      <section className="g-panel" aria-label="Остатки по продуктам">
        {shown.length === 0 && <p className="g-empty"><strong>Ничего не найдено</strong>Измените поиск или фильтр.</p>}
        {shown.map((p) => {
          const isOpen = open[p.productId] ?? (p.kind === 'expired' || p.kind === 'hot' || p.batches.some((b) => b.id === target));
          return (
            <div key={p.productId} className="prod">
              <button className="prow" type="button" aria-expanded={isOpen} onClick={() => setOpen((o) => ({ ...o, [p.productId]: !isOpen }))}>
                <span className="g-photo prow__photo" role="img" aria-label={`Место для фото: ${p.productName}`}>
                  <span aria-hidden="true">{initials(p.productName)}</span>
                </span>
                <span className="prow__name">
                  <span className="g-name">{p.productName}</span>
                  <span className="muted small">
                    {unitName(p.unit)} · хранение {shelfLife(p.product.shelfLifeHours)} · {money(p.product.cost ?? 0)}/{unitName(p.unit)}
                  </span>
                </span>
                <span className="hide-s prow__total">
                  <span className="num" style={{ color: p.kind === 'expired' || p.fresh === 0 ? 'var(--red)' : 'var(--h)' }}>{qty(p.fresh, p.unit)}</span>
                  <span className="muted small">годный остаток</span>
                </span>
                <span className="hide-s prow__state">
                  <span className={KINDS[p.kind].chip}>{KINDS[p.kind].word}</span>
                  <span className="muted small">{p.next}</span>
                </span>
                <svg className="ico" viewBox="0 0 24 24" aria-hidden="true" style={{ transform: `rotate(${isOpen ? 180 : 0}deg)` }}><path d="m6 9 6 6 6-6" /></svg>
              </button>
              {isOpen && (
                p.batches.length === 0 ? (
                  <p className="batch-row batch-row--empty muted">Партий нет. Продукт появится после приёмки поставки.</p>
                ) : (
                  <ul>
                    {p.batches.map((b) => {
                      const kind = batchKind(b);
                      return (
                        <li key={b.id} className={`batch-row${b.id === target ? ' batch-row--target' : ''}`} ref={b.id === target ? targetRow : undefined}>
                          <span className={KINDS[kind].chip}>{KINDS[kind].word}</span>
                          <span className="batch-row__main">
                            <strong className="num hd">{qty(b.remaining, b.unit)} из {qty(b.quantity, b.unit)}</strong>
                            <span className="muted small">
                              Поставка {b.supplyNumber} · принята {dayMonthShort(b.receivedAt)}{b.openedAt && ` · вскрыта ${dayMonthShort(b.openedAt)}`} · <Link to={`/app/labels?batch=${b.id}`}>этикетка</Link>
                            </span>
                          </span>
                          <span className="batch-row__until">
                            <span className="muted small">годен до</span>
                            <strong className="num hd">{dueLong(b.expiresAt)}</strong>
                          </span>
                          <span className="num r batch-row__left" style={{ color: KINDS[kind].color }}>{leftPrecise(b.hoursLeft)}</span>
                          {opening?.id === b.id ? (
                            <span className="batch-row__ask" role="group" aria-label="Вскрыть упаковку">
                              <span className="muted small">Сколько вскрыли? Срок станет {b.openedShelfLifeHours} ч</span>
                              <span className="g-unit batch-row__qty">
                                <input className="g-input num" inputMode="decimal" aria-label="Количество" value={opening.quantity} onChange={(e) => setOpening({ ...opening, quantity: e.target.value, error: '' })} />
                                <span>{unitName(b.unit)}</span>
                              </span>
                              <button className="g-btn g-btn--sm g-btn--primary" type="button" disabled={busy} onClick={openBatch}>Вскрыть</button>
                              <button className="g-btn g-btn--sm batch-row__cancel" type="button" onClick={() => setOpening(null)}>Отмена</button>
                              {opening.error && <span className="g-error-text" role="alert">{opening.error}</span>}
                            </span>
                          ) : asking === b.id ? (
                            <span className="batch-row__ask" role="group" aria-label="Причина списания">
                              <span className="muted small">Почему списываем?</span>
                              <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => writeOff(b, 'Spoiled')}>Порча при хранении</button>
                              <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => writeOff(b, 'Defective')}>Брак поставки</button>
                              <button className="g-btn g-btn--sm batch-row__cancel" type="button" onClick={() => setAsking(null)}>Отмена</button>
                            </span>
                          ) : (
                            // Просроченная партия списывается сразу: причина известна. Для годной спрашиваем.
                            <span className="batch-row__off batch-row__acts">
                            {b.openedShelfLifeHours && !b.openedAt && b.status !== 'Expired' && (
                              <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => { setAsking(null); setOpening({ id: b.id, quantity: String(b.remaining).replace('.', ','), error: '' }); }}>
                                Вскрыть
                              </button>
                            )}
                            <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => (b.status === 'Expired' ? writeOff(b) : setAsking(b.id))}>
                              Списать
                            </button>
                            </span>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                )
              )}
            </div>
          );
        })}
      </section>
    </>
  );
}

/** Достаёт идентификатор партии из текста QR-кода этикетки: это ссылка вида …/app/stock?batch=<id>. */
function batchFromLabel(text: string): string | null {
  try {
    return new URL(text).searchParams.get('batch');
  } catch {
    return null;
  }
}

/** Две буквы для плашки без фото: первые буквы двух слов или начало единственного слова. */
function initials(name: string): string {
  const words = name.replace(/[^А-Яа-яЁёA-Za-z ]/g, '').split(' ').filter((w) => w.length > 1);
  return (words.length > 1 ? words[0][0] + words[1][0] : (words[0] ?? name).slice(0, 2)).toUpperCase();
}
