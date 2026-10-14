import { useState, type FormEvent } from 'react';
import { enterRestaurant, send, useAction, useApi, type OwnerSummary, type RestaurantSummary } from '../api';
import { money, plural, qty } from '../format';
import { Fail, Period, Wait } from '../ui';

/** Сводка по всем ресторанам владельца за период: продажи, потери, что горит на складах. */
export function Summary() {
  const [days, setDays] = useState(7);
  const summary = useApi<OwnerSummary>(`/owner/summary?days=${days}`, 60_000);
  const data = summary.data;

  return (
    <>
      <div className="g-title">
        <h1>Сводка</h1>
        <span className="sub">{data ? plural(data.restaurants.length, 'ресторан', 'ресторана', 'ресторанов') : ''}</span>
        <span className="end"><Period days={days} onChange={setDays} /></span>
      </div>

      {!data ? <Wait error={summary.error} what="сводку" reload={summary.reload} /> : (
        <>
          <dl className="sum">
            <div className="g-kpi g-kpi--main"><dt>Выручка</dt><dd className="v num">{money(data.total.revenue)}</dd><dd className="n">{plural(data.total.orders, 'заказ', 'заказа', 'заказов')}</dd></div>
            <div className={`g-kpi${data.total.writeOffs.total > 0 ? ' g-kpi--red' : ''}`}><dt>Списано</dt><dd className="v num">{money(data.total.writeOffs.total)}</dd><dd className="n">по закупочной цене</dd></div>
            <div className={`g-kpi${data.total.stopCost > 0 ? ' g-kpi--red' : ''}`}><dt>Потери от стопов</dt><dd className="v num">{money(data.total.stopCost)}</dd><dd className="n">недополученная выручка</dd></div>
            <div className="g-kpi"><dt>На складах сейчас</dt><dd className="v num">{data.total.toWriteOff} / {data.total.expiringSoon}</dd><dd className="n">партий под списание / истекает за сутки</dd></div>
          </dl>

          <Restaurants rows={data.restaurants} total={data.total} />

          <section className="g-panel" aria-labelledby="top-h">
            <div className="g-panel__head"><h2 id="top-h">Чаще всего списывают</h2><span className="end">по всем ресторанам, топ-5 по сумме</span></div>
            {data.topWrittenOff.length === 0 ? (
              <p className="g-empty"><strong>Списаний за период нет</strong>Здесь появятся продукты, которые уходят в списание больше всего.</p>
            ) : (
              <div className="g-table-wrap">
                <table className="g-table top-table stackable">
                  <thead><tr><th>Продукт</th><th className="r">Списаний</th><th className="r">Количество</th><th className="r">Сумма</th><th className="r">Ресторанов</th></tr></thead>
                  <tbody>
                    {data.topWrittenOff.map((p) => (
                      <tr key={`${p.productName} ${p.unit}`}>
                        <td className="g-name stackable__head">{p.productName}</td>
                        <td className="r" data-label="Списаний">{p.times}</td>
                        <td className="r" data-label="Количество">{qty(p.quantity, p.unit)}</td>
                        <td className="r hd" data-label="Сумма">{money(p.value)}</td>
                        <td className="r" data-label="Ресторанов">{p.restaurants}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}

      <AddRestaurant />
    </>
  );
}

function Restaurants({ rows, total }: { rows: RestaurantSummary[]; total: RestaurantSummary }) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const open = async (id: string) => {
    setBusy(true);
    // При удаче открывается кабинет ресторана; сюда доходим только при ошибке.
    setError((await enterRestaurant(id).catch((e: Error) => e.message)) ?? '');
    setBusy(false);
  };

  const cells = (r: RestaurantSummary) => (
    <>
      <td className="r hd" data-label="Выручка">{money(r.revenue)}</td>
      <td className="r" data-label="Заказы">{r.orders}</td>
      <td className="r" data-label="Списано">
        <span className={r.writeOffs.total > 0 ? 'hd' : ''}>{money(r.writeOffs.total)}</span>
        {r.writeOffs.total > 0 && (
          <span className="muted small reasons">
            {r.writeOffs.expired > 0 && <span>срок {money(r.writeOffs.expired)}</span>}
            {r.writeOffs.spoiled > 0 && <span>порча {money(r.writeOffs.spoiled)}</span>}
            {r.writeOffs.defective > 0 && <span>брак {money(r.writeOffs.defective)}</span>}
          </span>
        )}
      </td>
      <td className={`r${r.stopCost > 0 ? ' over' : ''}`} data-label="Потери от стопов">{money(r.stopCost)}</td>
      <td className="r" data-label="В стопе">{r.stopped > 0 ? <span className="g-tag g-tag--red">{r.stopped}</span> : 0}</td>
      <td className="r" data-label="Под списание">{r.toWriteOff > 0 ? <span className="g-tag g-tag--red">{r.toWriteOff}</span> : 0}</td>
      <td className="r" data-label="Истекает за сутки">{r.expiringSoon > 0 ? <span className="g-tag g-tag--amber">{r.expiringSoon}</span> : 0}</td>
    </>
  );

  return (
    <section className="g-panel" aria-labelledby="rest-h">
      <div className="g-panel__head"><h2 id="rest-h">По ресторанам</h2><Fail text={error} /></div>
      <div className="g-table-wrap">
        <table className="g-table rest-table stackable">
          <thead>
            <tr>
              <th>Ресторан</th><th className="r">Выручка</th><th className="r">Заказы</th><th className="r">Списано</th>
              <th className="r">Потери от стопов</th><th className="r">В стопе</th><th className="r">Под списание</th><th className="r">Истекает за сутки</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td className="stackable__head">
                  <span className="rest-table__name">
                    <span className="g-name">{r.name}</span>
                    <button className="g-btn g-btn--sm" type="button" disabled={busy} onClick={() => void open(r.id)}>Открыть ресторан</button>
                  </span>
                </td>
                {cells(r)}
              </tr>
            ))}
            {rows.length > 1 && <tr className="rest-table__total"><td className="g-name stackable__head">Итого</td>{cells(total)}</tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function AddRestaurant() {
  const [name, setName] = useState('');
  const [done, setDone] = useState('');
  const { busy, error, run } = useAction();

  const add = (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setDone('');
    void run(() => send('POST', '/my/restaurants', { name }), () => {
      setDone(`Ресторан «${name.trim()}» добавлен. Откройте его, чтобы завести продукты, блюда и сотрудников.`);
      setName('');
    });
  };

  return (
    <section className="g-panel new-rest" aria-labelledby="add-h">
      <div className="g-panel__head"><h2 id="add-h">Добавить ресторан</h2></div>
      <form className="form-body" onSubmit={add} noValidate>
        <div className="g-field">
          <label htmlFor="rest-name">Название</label>
          <input className="g-input" id="rest-name" maxLength={120} value={name} onChange={(e) => setName(e.target.value)} />
          <span className="g-hint">Новый ресторан создаётся пустым: у него будут свои продукты, поставщики, блюда и сотрудники.</span>
        </div>
        {done && <div className="g-note g-note--ok" role="status"><span>{done}</span></div>}
        <Fail text={error} />
        <button className="g-btn g-btn--primary" type="submit" disabled={busy || !name.trim()}>Добавить</button>
      </form>
    </section>
  );
}
