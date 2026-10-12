import { useState, type FormEvent } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { send, useAction, useApi, type Credentials, type LeadCard, type LeadRow, type LeadStatus } from '../api';
import { dueLong, plural } from '../format';
import { Fail, Notes, STATUSES, Secret, Tasks, Wait } from '../ui';

/** Воронка: колонка на каждый статус; на телефоне видна одна, её выбирают переключателем. */
export function Leads() {
  const leads = useApi<LeadRow[]>('/crm/leads');
  const [params, setParams] = useSearchParams();
  const status = STATUSES.find((s) => s.id === params.get('status'))?.id ?? 'New';

  if (!leads.data) return <Wait error={leads.error} what="заявки" reload={leads.reload} />;
  const by = (id: LeadStatus) => leads.data!.filter((l) => l.status === id);

  return (
    <>
      <div className="g-title"><h1>Заявки</h1><span className="sub num">{leads.data.length}</span></div>

      <div className="g-seg funnel-pick" role="group" aria-label="Статус">
        {STATUSES.map((s) => (
          <button key={s.id} type="button" aria-pressed={s.id === status} onClick={() => setParams({ status: s.id }, { replace: true })}>
            {s.title} <span className="n">{by(s.id).length}</span>
          </button>
        ))}
      </div>

      {leads.data.length === 0 && <p className="g-empty"><strong>Заявок пока нет</strong>Они появятся, когда посетитель заполнит форму «Запросить демо» на сайте или вы создадите заявку из диалога.</p>}

      <div className="funnel">
        {STATUSES.map((s) => (
          <section key={s.id} className={`funnel__col${s.id === status ? ' funnel__col--picked' : ''}`} aria-label={s.title}>
            <h2>{s.title} <span className="muted num">{by(s.id).length}</span></h2>
            {by(s.id).length === 0 && <p className="muted small funnel__none">Пусто</p>}
            <ul>
              {by(s.id).map((l) => (
                <li key={l.id}>
                  <Link className="lead" to={`/leads/${l.id}`}>
                    <span className="g-name">{l.organization}</span>
                    <span>{l.name}</span>
                    <span className="muted small num">{l.contact}</span>
                    <span className="muted small">
                      <span className="num">{dueLong(l.createdAt)}</span>
                      {l.assignee && <> · {l.assignee}</>}
                    </span>
                    {(l.openTasks > 0 || l.notes > 0 || l.chatId) && (
                      <span className="muted small">
                        {[l.openTasks > 0 && plural(l.openTasks, 'задача', 'задачи', 'задач'), l.notes > 0 && plural(l.notes, 'заметка', 'заметки', 'заметок'), l.chatId && 'есть диалог']
                          .filter(Boolean).join(' · ')}
                      </span>
                    )}
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
    </>
  );
}

export function Lead() {
  const { id } = useParams();
  const lead = useApi<LeadCard>(`/crm/leads/${id}`);
  if (!lead.data) return <><Link className="back" to="/leads">← Заявки</Link><Wait error={lead.error} what="заявку" reload={lead.reload} /></>;
  return <Card key={lead.data.id} lead={lead.data} />;
}

function Card({ lead }: { lead: LeadCard }) {
  const [assignee, setAssignee] = useState(lead.assignee ?? '');
  const { busy, error, run } = useAction();
  const save = (status: LeadStatus) => run(() => send('PUT', `/crm/leads/${lead.id}`, { status, assignee: assignee.trim() || null }));

  return (
    <>
      <Link className="back" to={`/leads?status=${lead.status}`}>← Заявки</Link>
      <div className="g-title">
        <h1>{lead.organization}</h1>
        <span className="sub">заявка № <span className="num">{lead.id}</span> · <span className="num">{dueLong(lead.createdAt)}</span></span>
      </div>

      <div className="cols cols--side-first">
        <div className="cols__main">
          <section className="g-panel" aria-label="Заявка">
            <dl className="facts">
              <div><dt>Кто</dt><dd className="hd">{lead.name}</dd></div>
              <div><dt>Контакт</dt><dd className="hd num pick">{lead.contact}</dd></div>
              <div><dt>Точек</dt><dd className="hd">{lead.points}</dd></div>
              <div><dt>Комментарий</dt><dd className="hd">{lead.comment ?? '—'}</dd></div>
              <div><dt>Изменена</dt><dd className="num">{dueLong(lead.updatedAt)}</dd></div>
              <div>
                <dt>Диалог</dt>
                <dd>{lead.chatId ? <Link className="hd" to={`/chats/${lead.chatId}`}>Открыть переписку</Link> : 'нет — заявка пришла с формы'}</dd>
              </div>
            </dl>
          </section>
          <Notes items={lead.notesList} target={{ leadId: lead.id }} />
          <Tasks title="Задачи" items={lead.tasks} target={{ leadId: lead.id }} empty="Задач нет. Поставьте напоминание: когда перезвонить или провести демо." />
        </div>

        <aside className="cols__side">
          <section className="g-panel" aria-label="Статус и ответственный">
            <div className="g-panel__head"><h2>Статус</h2></div>
            <div className="form-body">
              <div className="statuses" role="group" aria-label="Статус заявки">
                {STATUSES.map((s) => (
                  <button key={s.id} className="g-btn g-btn--sm" type="button" aria-pressed={s.id === lead.status} disabled={busy || s.id === lead.status} onClick={() => void save(s.id)}>
                    {s.title}
                  </button>
                ))}
              </div>
              <form className="g-field" onSubmit={(e: FormEvent) => { e.preventDefault(); void save(lead.status); }}>
                <label htmlFor="assignee">Ответственный</label>
                <div className="line">
                  <input className="g-input" id="assignee" maxLength={60} placeholder="не назначен" value={assignee} onChange={(e) => setAssignee(e.target.value)} />
                  <button className="g-btn" type="submit" disabled={busy || assignee.trim() === (lead.assignee ?? '')}>Сохранить</button>
                </div>
              </form>
              <Fail text={error} />
            </div>
          </section>
          <Cabinet lead={lead} />
        </aside>
      </div>
    </>
  );
}

/** Кабинет по заявке: форма создаёт ресторан, привязывает его к заявке и переводит её в «Подключены». */
function Cabinet({ lead }: { lead: LeadCard }) {
  const [form, setForm] = useState({ restaurant: lead.organization === '—' ? '' : lead.organization, login: '' });
  const [created, setCreated] = useState<Credentials | null>(null);
  const { busy, error, run } = useAction();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void run(() => send('POST', `/crm/leads/${lead.id}/restaurant`, form), async (response) => setCreated(await response.json()));
  };

  return (
    <section className="g-panel" aria-label="Кабинет ресторана">
      <div className="g-panel__head"><h2>Кабинет</h2></div>
      <div className="form-body">
        {created && <Secret title={`Кабинет «${created.name}» открыт`} value={created} onClose={() => setCreated(null)} />}
        {lead.restaurantId ? (
          <p>Открыт: <Link className="g-name" to={`/restaurants/${lead.restaurantId}`}>{lead.restaurantName}</Link></p>
        ) : (
          <form className="stack" onSubmit={submit} noValidate>
            <div className="g-field">
              <label htmlFor="cab-name">Название ресторана</label>
              <input className="g-input" id="cab-name" value={form.restaurant} onChange={(e) => setForm({ ...form, restaurant: e.target.value })} />
            </div>
            <div className="g-field">
              <label htmlFor="cab-login">Логин управляющего</label>
              <input className="g-input" id="cab-login" autoCapitalize="none" autoComplete="off" value={form.login} onChange={(e) => setForm({ ...form, login: e.target.value })} />
              <span className="g-hint">Латинские буквы и цифры, например trattoria.admin. Пароль система выдаст сама и покажет один раз.</span>
            </div>
            <Fail text={error} />
            <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Открыть кабинет</button>
          </form>
        )}
      </div>
    </section>
  );
}
