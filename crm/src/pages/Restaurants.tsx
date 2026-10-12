import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { send, useAction, useApi, type Credentials, type RestaurantCard, type RestaurantRow } from '../api';
import { date, dueLong } from '../format';
import { Confirm, Fail, Notes, Secret, Tasks, Wait } from '../ui';

const ROLES: Record<RestaurantCard['users'][number]['role'], string> = {
  Manager: 'Управляющий',
  Chef: 'Шеф-повар',
  Cook: 'Повар',
  Storekeeper: 'Кладовщик',
  Waiter: 'Официант',
};

const State = ({ r }: { r: RestaurantRow }) => (
  <>
    {r.isDemo && <span className="g-tag g-tag--amber">демо</span>}
    {r.disabledAt && <span className="g-tag g-tag--red">отключён</span>}
  </>
);

export function Restaurants() {
  const list = useApi<RestaurantRow[]>('/crm/restaurants');
  if (!list.data) return <Wait error={list.error} what="рестораны" reload={list.reload} />;

  return (
    <>
      <div className="g-title"><h1>Рестораны</h1><span className="sub num">{list.data.length}</span></div>
      <section className="g-panel" aria-label="Рестораны">
          {list.data.length === 0 ? (
            <p className="g-empty"><strong>Ресторанов пока нет</strong>Создайте кабинет в форме «Новый ресторан» или из карточки заявки.</p>
          ) : (
            <div className="g-table-wrap">
              <table className="g-table rest-table">
                <thead>
                  <tr><th>Ресторан</th><th>Управляющий</th><th className="r">Сотрудников</th><th className="r">Продуктов</th><th className="r">Заказов за 7 дней</th><th>Последняя активность</th><th>Создан</th></tr>
                </thead>
                <tbody>
                  {list.data.map((r) => (
                    <tr key={r.id} className={r.disabledAt ? 'rest-table__off' : undefined}>
                      <td><span className="rest-table__name"><Link className="g-name" to={`/restaurants/${r.id}`}>{r.name}</Link><State r={r} /></span></td>
                      <td className="num">{r.managerLogin ?? '—'}</td>
                      <td className="r">{r.staff}</td>
                      <td className="r">{r.products}</td>
                      <td className="r">{r.ordersLast7Days}</td>
                      <td className="num">{r.lastActivityAt ? dueLong(r.lastActivityAt) : 'не было'}</td>
                      <td className="num muted">{date(r.createdAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      </section>
      <NewRestaurant />
    </>
  );
}

/** Кабинет новому ресторану открываем мы: система выдаёт пароль управляющего, его передают клиенту. */
function NewRestaurant() {
  const [form, setForm] = useState({ restaurant: '', login: '' });
  const [created, setCreated] = useState<Credentials | null>(null);
  const { busy, error, run } = useAction();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void run(() => send('POST', '/crm/restaurants', form), async (response) => {
      setCreated(await response.json());
      setForm({ restaurant: '', login: '' });
    });
  };

  return (
    <form className="g-panel new-rest" onSubmit={submit} noValidate>
      <div className="g-panel__head"><h2>Новый ресторан</h2></div>
      <div className="form-body">
        <div className="g-field">
          <label htmlFor="nr-name">Название ресторана</label>
          <input className="g-input" id="nr-name" value={form.restaurant} onChange={(e) => setForm({ ...form, restaurant: e.target.value })} />
        </div>
        <div className="g-field">
          <label htmlFor="nr-login">Логин управляющего</label>
          <input className="g-input" id="nr-login" autoCapitalize="none" autoComplete="off" value={form.login} onChange={(e) => setForm({ ...form, login: e.target.value })} />
          <span className="g-hint">Латинские буквы и цифры, например trattoria.admin. Пароль система выдаст сама.</span>
        </div>
        <Fail text={error} />
        {created && <Secret title={`Ресторан «${created.name}» создан`} value={created} onClose={() => setCreated(null)} />}
        <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Создать</button>
      </div>
    </form>
  );
}

export function Restaurant() {
  const { id } = useParams();
  const card = useApi<RestaurantCard>(`/crm/restaurants/${id}`);
  if (!card.data) return <><Link className="back" to="/restaurants">← Рестораны</Link><Wait error={card.error} what="ресторан" reload={card.reload} /></>;
  return <Card key={card.data.id} r={card.data} />;
}

function Card({ r }: { r: RestaurantCard }) {
  const [reset, setReset] = useState<Credentials | null>(null);
  const { busy, error, run } = useAction();
  const access = useAction();
  const path = `/crm/restaurants/${r.id}`;

  return (
    <>
      <Link className="back" to="/restaurants">← Рестораны</Link>
      <div className="g-title">
        <h1>{r.name}</h1>
        <State r={r} />
        <span className="sub">создан <span className="num">{date(r.createdAt)}</span></span>
      </div>

      <div className="cols">
        <div className="cols__main">
          <section className="g-panel" aria-label="Активность">
            <dl className="facts">
              <div><dt>Последняя активность</dt><dd className="hd num">{r.lastActivityAt ? dueLong(r.lastActivityAt) : 'не было'}</dd></div>
              <div><dt>Заказов за 7 дней</dt><dd className="hd num">{r.ordersLast7Days}</dd></div>
              <div><dt>Продуктов в справочнике</dt><dd className="hd num">{r.products}</dd></div>
              <div><dt>Заявка</dt><dd>{r.leadId !== null ? <Link className="hd" to={`/leads/${r.leadId}`}>№ {r.leadId}</Link> : 'нет — кабинет создан без заявки'}</dd></div>
            </dl>
          </section>

          <section className="g-panel" aria-label="Сотрудники">
            <div className="g-panel__head"><h2>Сотрудники</h2><span className="end num">{r.users.length}</span></div>
            {reset && <div className="form-body"><Secret title={`Пароль для «${reset.login}» сброшен`} value={reset} onClose={() => setReset(null)} /></div>}
            {error && <div className="form-body"><Fail text={error} /></div>}
            <div className="g-table-wrap">
              <table className="g-table">
                <thead><tr><th>Логин</th><th>Должность</th><th>Роль</th><th /></tr></thead>
                <tbody>
                  {r.users.map((u) => (
                    <tr key={u.login}>
                      <td className="g-name num">{u.login}</td>
                      <td>{u.title}</td>
                      <td>{ROLES[u.role]}</td>
                      <td className="r">
                        {/* Пароли остальных сотрудников меняет управляющий ресторана в своём кабинете. */}
                        {u.role === 'Manager' && !r.isDemo && (
                          <Confirm label="Сбросить пароль" ask="Старый пароль перестанет работать. Сбросить?" yes="Сбросить" busy={busy}
                            onConfirm={() => run(() => send('POST', `${path}/reset-password`, { login: u.login }), async (response) => setReset(await response.json()))} />
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <Notes items={r.notesList} target={{ restaurantId: r.id }} />
          <Tasks title="Задачи" items={r.tasks} target={{ restaurantId: r.id }} empty="Задач по ресторану нет." />
        </div>

        <aside className="cols__side">
          <section className="g-panel" aria-label="Доступ">
            <div className="g-panel__head"><h2>Доступ</h2></div>
            <div className="form-body">
              {r.isDemo ? (
                <p>Демо-ресторан: отключить его и сбросить в нём пароли нельзя.</p>
              ) : r.disabledAt ? (
                <>
                  <p>Отключён <span className="num">{dueLong(r.disabledAt)}</span>. Сотрудники войти не могут, данные сохранены.</p>
                  <Confirm label="Включить ресторан" ask="Сотрудники снова смогут войти. Включить?" yes="Включить" busy={access.busy} onConfirm={() => access.run(() => send('POST', `${path}/enable`))} />
                </>
              ) : (
                <>
                  <p>Работает. После отключения сотрудники не смогут войти, а открытые сессии закроются. Данные сохранятся.</p>
                  <Confirm label="Отключить ресторан" ask={`Отключить «${r.name}»?`} yes="Отключить" busy={access.busy} onConfirm={() => access.run(() => send('POST', `${path}/disable`))} />
                </>
              )}
              <Fail text={access.error} />
            </div>
          </section>
        </aside>
      </div>
    </>
  );
}
