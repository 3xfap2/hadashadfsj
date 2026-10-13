import { useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { problemText, send, useApi, type DishRow, type HotOffer, type HotRules } from '../api';
import { money, portions } from '../format';
import { Photo } from '../ui';

const DEFAULTS: HotRules = { horizonHours: 48, urgencyWeight: 50, shareAcrossDishes: true, maxDiscountPercent: 40 };

/** Настройка горячих предложений: правила расчёта слева, получившийся список и его ручная правка справа. */
export function HotSettings() {
  const rules = useApi<HotRules>('/hot-offers/settings', 300_000);
  const offers = useApi<HotOffer[]>('/hot-offers');
  const dishes = useApi<DishRow[]>('/dishes');
  const [form, setForm] = useState<HotRules | null>(null);
  const [note, setNote] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!form && rules.data) setForm(rules.data);
  }, [form, rules.data]);

  const error = rules.error ?? offers.error ?? dishes.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить настройки</h3><span>{error}</span></div>;
  if (!form || !offers.data || !dishes.data) return <p className="muted">Загружаем настройки…</p>;

  const save = async (next: HotRules) => {
    setBusy(true);
    // Обрыв связи бросает исключение: без finally кнопки остались бы серыми до перезагрузки страницы.
    try {
      const response = await send('PUT', '/hot-offers/settings', next);
      if (response.ok) {
        setForm(next);
        setNote({ kind: 'ok', text: 'Сохранено. Список справа пересчитан.' });
        await offers.reload();
      } else {
        setNote({ kind: 'err', text: await problemText(response) });
      }
    } catch (e) {
      setNote({ kind: 'err', text: (e as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const mark = async (dishId: string, pinned: boolean, hidden: boolean) => {
    setBusy(true);
    try {
      const response = await send('PUT', `/hot-offers/dishes/${dishId}`, { pinned, hidden });
      // Отказ сервера показываем, а не глотаем: иначе кажется, что блюдо закреплено или скрыто.
      setNote(response.ok ? null : { kind: 'err', text: await problemText(response) });
      await Promise.all([offers.reload(), dishes.reload()]);
    } catch (e) {
      setNote({ kind: 'err', text: (e as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const hidden = dishes.data.filter((d) => d.hotHidden);
  const balance = form.urgencyWeight === 50 ? 'поровну' : form.urgencyWeight < 50 ? 'важнее сумма потерь' : 'важнее срок';

  return (
    <>
      <div className="buy-head">
        <h1>Горячие предложения</h1>
        <p className="lead-text">Правила, по которым система решает, что продавать в первую очередь. Список можно поправить и вручную. <Link to="/app">К экрану «Сегодня»</Link></p>
      </div>

      <div className="sup-row">
        <aside className="sup-side cat-side">
          <form className="g-panel" onSubmit={(e: FormEvent) => { e.preventDefault(); void save(form); }} noValidate>
            <div className="g-panel__head"><h2>Правила</h2></div>
            <div className="form-body">
              <div className="g-field">
                <label htmlFor="h-horizon">Горизонт</label>
                <div className="g-unit">
                  <input className="g-input num" id="h-horizon" inputMode="numeric" value={form.horizonHours} onChange={(e) => setForm({ ...form, horizonHours: Number(e.target.value) || 0 })} />
                  <span>ч</span>
                </div>
                <span className="g-hint">Учитываются партии, которые истекают в эти часы. От 6 до 168.</span>
              </div>
              <div className="g-field">
                <label htmlFor="h-weight">Что важнее: <span className="hot-balance">{balance}</span></label>
                <input className="hot-range" id="h-weight" type="range" min={0} max={100} step={10} value={form.urgencyWeight} onChange={(e) => setForm({ ...form, urgencyWeight: Number(e.target.value) })} />
                <div className="hot-range__ends muted small"><span>сумма потерь</span><span>срок</span></div>
                <span className="g-hint">Слева блюда идут по рублям, которые пропадут. Справа — по тому, как скоро истекает продукт.</span>
              </div>
              <label className="hot-check">
                <input type="checkbox" checked={form.shareAcrossDishes} onChange={(e) => setForm({ ...form, shareAcrossDishes: e.target.checked })} />
                <span>Понижать блюдо, если тот же продукт уходит и в другие блюда</span>
              </label>
              <div className="g-field">
                <label htmlFor="h-discount">Наибольшая скидка</label>
                <div className="g-unit">
                  <input className="g-input num" id="h-discount" inputMode="numeric" value={form.maxDiscountPercent} onChange={(e) => setForm({ ...form, maxDiscountPercent: Number(e.target.value) || 0 })} />
                  <span>%</span>
                </div>
                <span className="g-hint">0 — скидки не предлагать. Ниже стоимости продуктов, которые не истекают, цена не опустится в любом случае.</span>
              </div>
              {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
            </div>
            <div className="g-panel__foot cat-foot">
              <button className="g-btn" type="button" disabled={busy} onClick={() => save(DEFAULTS)}>Вернуть исходные</button>
              <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Сохранить</button>
            </div>
          </form>
        </aside>

        <div className="sup-main">
          <section className="g-panel" aria-labelledby="hot-list-h">
            <div className="g-panel__head">
              <h2 id="hot-list-h">Список сейчас</h2>
              <span className="end">по сохранённым правилам</span>
            </div>
            {offers.data.length === 0 ? (
              <p className="g-empty"><strong>Предложений нет</strong>В пределах горизонта ничего не истекает, либо все такие блюда скрыты.</p>
            ) : (
              <ul className="g-rows">
                {offers.data.map((o, i) => (
                  <li key={o.dishId} className="hot-row">
                    <span className="hot-row__rank num">{i + 1}</span>
                    <Photo url={o.photoUrl} name={o.dishName} />
                    <div className="hot-row__text">
                      <span className="g-name">{o.dishName}{o.pinned && <span className="g-tag g-tag--amber hot-row__pin">Закреплено</span>}</span>
                      <span className="g-why">{o.reason}</span>
                      <span className="muted small num">
                        приоритет {Math.round(o.score).toLocaleString('ru-RU')} · {portions(o.portions)} · {money(o.valueAtRisk ?? 0)} под списание
                        {o.discount && ` · скидка ${o.discount.percent}%`}
                      </span>
                    </div>
                    <div className="hot-row__acts">
                      <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => mark(o.dishId, !o.pinned, false)}>{o.pinned ? 'Открепить' : 'Закрепить первым'}</button>
                      <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => mark(o.dishId, false, true)}>Скрыть</button>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {hidden.length > 0 && (
            <section className="g-panel" aria-labelledby="hot-hidden-h">
              <div className="g-panel__head"><h2 id="hot-hidden-h">Скрытые блюда</h2><span className="end">не предлагаются, но продаются как обычно</span></div>
              <ul className="g-rows">
                {hidden.map((d) => (
                  <li key={d.id} className="hot-row hot-row--plain">
                    <span className="g-name">{d.name}</span>
                    <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => mark(d.id, false, false)}>Вернуть в предложения</button>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section className="g-panel" aria-labelledby="hot-how-h">
            <div className="g-panel__head"><h2 id="hot-how-h">Как считается приоритет</h2></div>
            <div className="form-body hot-how">
              <p>Для каждого блюда в продаже система берёт продукты из его техкарты, у которых партии истекают в пределах горизонта, и складывает их вклады:</p>
              <pre>вклад продукта = количество, которое блюдо успеет израсходовать × цена × срочность ÷ √N</pre>
              <ul>
                <li><b>Количество</b> ограничено остатком истекающих партий и числом порций, на которое хватает остальных продуктов блюда. Чем больше продукта идёт в порцию, тем больше блюдо спасает.</li>
                <li><b>Срочность</b> = 1 − часов до конца срока ÷ горизонт. Ползунок «что важнее» меняет её вес: в крайнем левом положении срочность не учитывается, в правом — учитывается вдвойне.</li>
                <li><b>N</b> — сколько блюд в продаже используют этот продукт. Если блюдо единственное, его вклад не делится.</li>
              </ul>
              <p>Блюда из стоп-листа в список не попадают. Скидка предлагается, только если при обычном спросе партия не успеет продаться до конца срока.</p>
            </div>
          </section>
        </div>
      </div>
    </>
  );
}
