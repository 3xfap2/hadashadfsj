import { useState } from 'react';
import { useApi, type Task } from '../api';
import { Tasks, Wait } from '../ui';

export function TasksPage() {
  const [all, setAll] = useState(false);
  const tasks = useApi<Task[]>(all ? '/crm/tasks' : '/crm/tasks?open=true');
  const overdue = tasks.data?.filter((t) => !t.doneAt && Date.parse(t.dueAt) < Date.now()).length ?? 0;

  return (
    <>
      <div className="g-title">
        <h1>Задачи</h1>
        {overdue > 0 && <span className="sub">просрочено: <b className="num over">{overdue}</b></span>}
        <div className="end">
          <div className="g-seg" role="group" aria-label="Какие задачи показать">
            <button type="button" aria-pressed={!all} onClick={() => setAll(false)}>Открытые</button>
            <button type="button" aria-pressed={all} onClick={() => setAll(true)}>Все</button>
          </div>
        </div>
      </div>
      {tasks.data
        ? <Tasks title={all ? 'Все задачи' : 'Открытые задачи'} items={tasks.data} link empty="Задач нет. Добавьте напоминание ниже — или из карточки заявки или ресторана, чтобы оно было к ним привязано." />
        : <Wait error={tasks.error} what="задачи" reload={tasks.reload} />}
    </>
  );
}
