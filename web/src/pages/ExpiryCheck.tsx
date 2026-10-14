import { useEffect, useState } from 'react';
import { post, problemText, useApi, type DishRow, type ExpiryCheck as Check, type ProductRow, type StockRow, type StopListRow } from '../api';
import { portions, qty } from '../format';

const seconds = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const at = (iso: string | number) => seconds.format(new Date(iso));
const STOP_REASON = { Expired: 'просрочено', OutOfStock: 'нет на складе', NotEnough: 'не хватает на порцию', Manual: 'снято вручную' };

/** «1:43» — сколько осталось до момента. */
function left(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

/**
 * Проверка сроков: партия, которая истекает через несколько минут. На ней за пару минут виден весь путь,
 * который обычная партия проходит за сутки: сигнал, срок истёк, блюдо в стопе, партия под списание, человек её списывает
 * (или автосписание, если его включили в настройках ресторана).
 */
export function ExpiryCheck() {
  const products = useApi<ProductRow[]>('/products', 300_000);
  const dishes = useApi<DishRow[]>('/dishes', 300_000);
  const stock = useApi<StockRow[]>('/stock');
  const stopList = useApi<StopListRow[]>('/stop-list');
  const check = useApi<Check | null>('/simulator/expiry-check');
  const [chosen, setChosen] = useState('');
  const [minutes, setMinutes] = useState('2');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [writeOffError, setWriteOffError] = useState('');
  const [now, setNow] = useState(Date.now);

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  // В момент истечения в базе ничего не меняется, поэтому экран перечитывает стоп-лист сам.
  const expiresAt = check.data ? new Date(check.data.expiresAt).getTime() : 0;
  const reloadStops = stopList.reload;
  useEffect(() => {
    const wait = expiresAt - Date.now();
    if (wait <= 0) return;
    const timer = setTimeout(reloadStops, wait + 500);
    return () => clearTimeout(timer);
  }, [expiresAt, reloadStops]);

  if (!products.data || !dishes.data || !stock.data || !stopList.data) return null;

  const used = products.data.filter((p) => dishes.data!.some((d) => d.recipe.some((r) => r.productId === p.id)));
  const fresh = (id: string) => stock.data!.find((s) => s.productId === id)?.fresh ?? 0;
  // Нагляднее всего продукт, которого сейчас нет: его блюдо вернётся в продажу и снова уйдёт в стоп.
  const productId = chosen || check.data?.productId || (used.find((p) => fresh(p.id) === 0) ?? used[0])?.id || '';

  const start = async () => {
    if (!(Number(minutes) >= 1 && Number(minutes) <= 30)) return setError('Укажите от 1 до 30 минут.');
    setBusy(true);
    setError('');
    // Обрыв связи бросает исключение: без finally кнопка осталась бы серой до перезагрузки страницы.
    try {
      const response = await post('/simulator/expiry-check', { productId, minutes: Number(minutes) });
      if (!response.ok) setError(`Сервер ответил ошибкой ${response.status}.`);
      await Promise.all([check.reload(), stock.reload(), stopList.reload()]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const c = check.data;
  const expired = c ? now >= expiresAt : false;
  const createdAt = c ? new Date(c.createdAt).getTime() : 0;
  // Если автосписание включено, просрочка ждёт человека отсрочку из настроек, но не дольше, чем хранилась; проверка идёт раз в 20 секунд.
  const writeOffAt = c ? expiresAt + Math.min(c.autoWriteOffAfterHours * 3_600_000, expiresAt - createdAt) : 0;
  const byHand = Boolean(c?.writtenOffBy && c.writtenOffBy !== 'система');

  const writeOff = async () => {
    if (!c) return;
    setBusy(true);
    setWriteOffError('');
    try {
      const response = await post(`/stock/batches/${c.batchId}/write-off`);
      if (!response.ok) setWriteOffError(`Не списано. ${await problemText(response)}`);
      await Promise.all([check.reload(), stock.reload(), stopList.reload()]);
    } catch (e) {
      setWriteOffError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  const affected = c ? stopList.data.filter((d) => dishes.data!.find((x) => x.id === d.dishId)?.recipe.some((r) => r.productId === c.productId)) : [];

  return (
    <section className="g-panel" aria-labelledby="check-h">
      <div className="g-panel__head">
        <h2 id="check-h">Проверка сроков</h2>
        <span className="end">партия, которая истекает через несколько минут</span>
      </div>
      <div className="form-body">
        <p className="muted check__lead">
          Обычная партия живёт сутки и дольше. Здесь можно привезти партию на пару минут и увидеть весь путь: сигнал о сроке, срок истёк, блюдо в стопе,
          партия «под списание» — и человек её списывает. Сама просрочка со склада не уходит, если автосписание не включено в настройках ресторана.
        </p>
        <div className="check__form">
          <div className="g-field">
            <label htmlFor="check-product">Продукт</label>
            <select className="g-input" id="check-product" value={productId} onChange={(e) => setChosen(e.target.value)}>
              {used.map((p) => <option key={p.id} value={p.id}>{p.name}{fresh(p.id) === 0 ? ' — сейчас нет годного' : ''}</option>)}
            </select>
          </div>
          <div className={`g-field${error ? ' g-field--error' : ''}`}>
            <label htmlFor="check-min">Срок годности</label>
            <div className="g-unit">
              <input className="g-input num" id="check-min" inputMode="numeric" value={minutes} onChange={(e) => setMinutes(e.target.value)} />
              <span>мин</span>
            </div>
          </div>
          <button className="g-btn g-btn--primary" type="button" disabled={busy || !productId} onClick={start}>Привезти партию</button>
        </div>
        {error && <span className="g-error-text" role="alert">{error}</span>}
        {fresh(productId) > 0 && !c && <span className="g-hint">У этого продукта есть другие годные партии: блюдо уйдёт в стоп, только когда закончатся и они.</span>}

        {c && (
          <ol className="check__steps">
            <li className="done">
              <strong>Партия {c.supplyNumber} принята в <span className="num">{at(c.createdAt)}</span></strong>
              <span>{c.productName}, <span className="num">{qty(c.quantity, c.unit)}</span> · годна до <span className="num">{at(c.expiresAt)}</span></span>
            </li>
            <li className={expired ? 'done' : 'now'}>
              {expired ? (
                <strong>Срок истёк в <span className="num">{at(c.expiresAt)}</span></strong>
              ) : (
                <strong>До конца срока <span className="num">{left(expiresAt - now)}</span></strong>
              )}
              <span>{expired ? 'Партия осталась на складе, но в заказы и заготовки больше не идёт. В колокольчике — сигнал «Под списание».' : 'Блюда с этим продуктом — наверху «Продать в первую очередь». В колокольчике — сигнал «срок годности истекает».'}</span>
            </li>
            <li className={expired ? 'done' : ''}>
              <strong>Блюда с этим продуктом</strong>
              {affected.length === 0 && <span>Продукт не входит ни в одно блюдо.</span>}
              {affected.map((d) => (
                <span key={d.dishId}>
                  {d.dishName}:{' '}
                  {d.status === 'Stop'
                    ? <b className="check__stop">в стопе — {d.limitingProductName.toLowerCase()}: {STOP_REASON[d.reason!]}</b>
                    : <>в продаже, <span className="num">{portions(d.portions)}</span></>}
                </span>
              ))}
            </li>
            <li className={c.writtenOffAt || c.remaining === 0 ? 'done' : expired ? 'now' : ''}>
              {c.writtenOffAt ? (
                <strong>Партия была под списание до <span className="num">{at(c.writtenOffAt)}</span></strong>
              ) : c.remaining === 0 ? (
                <strong>Партию продали раньше срока — списывать нечего</strong>
              ) : (
                <strong>Партия под списание{!expired && ' — после истечения срока'}</strong>
              )}
              <span>
                {c.writtenOffAt || c.remaining === 0
                  ? 'Пока партию не списали, она была видна на складе и на экране «Сегодня» со статусом «Под списание».'
                  : 'Она на складе и на «Сегодня» со статусом «Под списание»: в заказ её взять нельзя, а списывает её человек — так он видит, что выбрасывает.'}
              </span>
              {expired && !c.writtenOffAt && c.remaining > 0 && (
                <span className="check__act">
                  <button className="g-btn g-btn--sm g-btn--primary" type="button" disabled={busy} onClick={writeOff}>Списать партию</button>
                  {writeOffError && <span className="g-error-text" role="alert">{writeOffError}</span>}
                </span>
              )}
            </li>
            <li className={c.writtenOffAt ? 'done' : ''}>
              {c.writtenOffAt ? (
                <strong>
                  {byHand ? <>Списал {c.writtenOffBy}</> : 'Списана автоматически'} в <span className="num">{at(c.writtenOffAt)}</span>
                </strong>
              ) : (
                <strong>
                  Списание: вручную
                  {c.autoWriteOff && c.remaining > 0 && <> или автоматически {expired ? <>около <span className="num">{at(writeOffAt + 20_000)}</span></> : 'после отсрочки'}</>}
                </strong>
              )}
              <span>
                {c.writtenOffAt
                  ? `Запись «Истёк срок годности${byHand ? '' : ' (автосписание)'}» — в журнале склада и в аналитике списаний.`
                  : c.autoWriteOff
                    ? `Автосписание включено в настройках ресторана: просрочка ждёт человека ${c.autoWriteOffAfterHours} ч (но не дольше, чем хранилась), потом списывается сама.`
                    : 'Автосписание выключено (так по умолчанию). Его может включить управляющий в разделе «Ресторан».'}
              </span>
            </li>
          </ol>
        )}
      </div>
    </section>
  );
}
