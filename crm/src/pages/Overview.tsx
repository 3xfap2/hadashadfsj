import { Link } from 'react-router-dom';
import { useApi, type Summary, type Task } from '../api';
import { dayEnd } from '../format';
import { STATUSES, Tasks, Wait } from '../ui';

export function Overview() {
  const summary = useApi<Summary>('/crm/summary');
  const tasks = useApi<Task[]>('/crm/tasks?open=true');
  const s = summary.data;
  if (!s) return <Wait error={summary.error} what="сводку" reload={summary.reload} />;

  // Просроченные и сегодняшние: сервер отдаёт открытые задачи по сроку, остаётся отрезать будущие дни.
  const due = tasks.data?.filter((t) => Date.parse(t.dueAt) < dayEnd());

  return (
    <>
      <div className="g-title"><h1>Обзор</h1></div>

      <section aria-label="Воронка заявок">
        <ul className="funnel-sum">
          {STATUSES.map((st) => (
            <li key={st.id}>
              <Link className={`g-kpi${st.id === 'New' && s.leads.new > 0 ? ' g-kpi--main' : ''}`} to={`/leads?status=${st.id}`}>
                <span>{st.title}</span>
                <span className="v">{s.leads[st.key]}</span>
              </Link>
            </li>
          ))}
        </ul>
      </section>

      <ul className="sum">
        <li>
          <Link className={`g-kpi${s.unreadChats > 0 ? ' g-kpi--red' : ''}`} to="/chats">
            <span>Диалоги без ответа</span>
            <span className="v">{s.unreadChats}</span>
          </Link>
        </li>
        <li>
          <Link className={`g-kpi${s.tasksOverdue > 0 ? ' g-kpi--red' : ''}`} to="/tasks">
            <span>Просроченные задачи</span>
            <span className="v">{s.tasksOverdue}</span>
          </Link>
        </li>
        <li>
          <Link className="g-kpi" to="/tasks">
            <span>Задачи на сегодня</span>
            <span className="v">{s.tasksToday}</span>
          </Link>
        </li>
        <li>
          <Link className="g-kpi" to="/restaurants">
            <span>Рестораны</span>
            <span className="v">{s.restaurants}</span>
            <span className="n">работали за 7 дней: <span className="num">{s.restaurantsActive7d}</span></span>
          </Link>
        </li>
      </ul>

      {due
        ? <Tasks title="Задачи: просроченные и на сегодня" items={due} link canAdd={false} empty="На сегодня задач нет." end={<Link to="/tasks">все задачи</Link>} />
        : <Wait error={tasks.error} what="задачи" reload={tasks.reload} />}
    </>
  );
}
