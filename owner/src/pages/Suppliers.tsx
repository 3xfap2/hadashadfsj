import { useState } from 'react';
import { useApi, type Suppliers as Rating } from '../api';
import { money, percent, plural, price, unitName } from '../format';
import { Period, Wait } from '../ui';

/** Рейтинг поставщиков по всем ресторанам: от лучшего к худшему, с объяснением оценки. */
export function Suppliers() {
  const [days, setDays] = useState(30);
  const rating = useApi<Rating>(`/owner/suppliers?days=${days}`, 120_000);
  const data = rating.data;

  return (
    <>
      <div className="g-title">
        <h1>Поставщики</h1>
        <span className="sub">{data ? `поставлено на ${money(data.delivered)}` : ''}</span>
        <span className="end"><Period days={days} onChange={setDays} /></span>
      </div>

      {!data ? <Wait error={rating.error} what="рейтинг поставщиков" reload={rating.reload} /> : (
        <>
          <div className="g-note g-note--ok formula">
            <h3>Как считается оценка</h3>
            <span>{data.formula}</span>
          </div>

          <section className="g-panel" aria-label="Рейтинг поставщиков">
            {data.suppliers.length === 0 ? (
              <p className="g-empty"><strong>Поставок за период нет</strong>Рейтинг появится, когда рестораны примут поставки с указанием поставщика.</p>
            ) : (
              <div className="g-table-wrap">
                <table className="g-table supplier-table stackable">
                  <thead>
                    <tr>
                      <th className="r">Место</th><th>Поставщик</th><th className="r">Оценка</th><th className="r">Поставки</th><th className="r">Сумма</th>
                      <th className="r">Брак</th><th className="r">Порча раньше срока</th><th className="r">Просрочка</th><th>Основные продукты, средняя цена</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.suppliers.map((s) => (
                      <tr key={s.supplier}>
                        <td className="r num hd" data-label="Место">{s.place}</td>
                        <td className="stackable__head">
                          <span className="g-name">{s.supplier}</span>
                          <span className="muted small supplier-table__sub">{plural(s.restaurants, 'ресторан', 'ресторана', 'ресторанов')} · срок при приёмке {s.shelfLifePercent} %</span>
                        </td>
                        <td className="r" data-label="Оценка"><span className={`g-tag score score--${s.score >= 90 ? 'ok' : s.score >= 70 ? 'mid' : 'bad'}`}>{s.score}</span></td>
                        <td className="r" data-label="Поставки">{s.supplies}</td>
                        <td className="r hd" data-label="Сумма">{money(s.delivered)}</td>
                        <td className={`r${s.defectivePercent > 0 ? ' over' : ''}`} data-label="Брак">{percent(s.defectivePercent)}</td>
                        <td className={`r${s.spoiledPercent > 0 ? ' over' : ''}`} data-label="Порча раньше срока">{percent(s.spoiledPercent)}</td>
                        <td className="r" data-label="Просрочка">{percent(s.expiredPercent)}</td>
                        <td className="stackable__head" data-label="Основные продукты, средняя цена">
                          <ul className="small supplier-table__products">
                            {s.products.map((p) => <li key={`${p.name} ${p.unit}`}>{p.name} — <span className="num">{price(p.averagePrice)}/{unitName(p.unit)}</span></li>)}
                          </ul>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </>
  );
}
