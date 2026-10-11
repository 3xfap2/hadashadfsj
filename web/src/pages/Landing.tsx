import { useState, type CSSProperties, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { login } from '../api';
import { LandingChat } from '../LandingChat';

// Открытая учётная запись демо-ресторана — та же, что на экране входа.
const DEMO = { login: 'demo', password: 'demo2026' };

const WRITE_OFFS = [620, 0, 1240, 380, 910, 0, 2050, 760, 430, 1180, 0, 890, 1560, 833];
const cols = (template: string): CSSProperties => ({ gridTemplateColumns: template });
const stack = (gap: number): CSSProperties => ({ display: 'flex', flexDirection: 'column', gap, minWidth: 0 });
const big: CSSProperties = { height: 52, padding: '0 24px', fontSize: 17 };

export function Landing() {
  const navigate = useNavigate();
  const [opening, setOpening] = useState(false);

  const openDemo = async () => {
    setOpening(true);
    const error = await login(DEMO.login, DEMO.password);
    setOpening(false);
    // Если демо-вход не удался, экран входа покажет, что именно не так.
    if (error) navigate('/login', { state: { error } });
    else navigate('/app');
  };

  return (
    <div className="g landing" style={{ background: '#ffffff' }}>
      <header className="lh">
        <div className="w">
          <a className="g-brand" href="#top">Остаток</a>
          <nav aria-label="Разделы страницы">
            <a href="#features">Возможности</a>
            <a href="#why">Почему мы</a>
            <a href="#rollout">Внедрение</a>
            <a href="#integrations">Интеграции</a>
            <a href="#faq">Вопросы</a>
          </nav>
          <div className="end">
            <Link className="g-btn" to="/login">Войти</Link>
            <a className="g-btn g-btn--primary" href="#request">Запросить демо</a>
          </div>
        </div>
      </header>

      <main id="top">
        <section className="hero">
          <div className="w">
            <h1 className="h1">Сроки каждой партии, стоп-лист и закупки — в одной системе для кухни</h1>
            <p className="lead" style={{ marginTop: 24 }}>
              Остаток списывает продукты из партии с ближайшим сроком, сам ставит блюда в стоп и подсказывает, что продать
              сегодня, пока продукт не пришлось выбрасывать.
            </p>
            <div className="hero__cta">
              <a className="g-btn g-btn--primary" href="#request" style={big}>
                Запросить демо <span className="arr" aria-hidden="true">→</span>
              </a>
              <button className="g-btn" type="button" style={big} onClick={openDemo} disabled={opening}>
                {opening ? 'Открываем…' : 'Открыть демо-ресторан'}
              </button>
            </div>
            <div className="stage">
              <div className="shot-cap">
                <span className="live"><i aria-hidden="true" />Демо-ресторан на 60 мест · данные смены</span>
              </div>
              <div className="shot" aria-label="Экран кабинета «Сегодня»">
                <div className="shot__in" aria-hidden="true">
                  <img src="/photos/cabinet-today.webp" alt="" width={1280} height={980} />
                </div>
              </div>
            </div>
          </div>
        </section>

        <section className="sec sec--panel" aria-labelledby="shift-h">
          <div className="w">
            <div className="shift-top">
              <div>
                <span className="kick">Как бывает сейчас</span>
                <h2 className="h2" id="shift-h" style={{ marginTop: 10 }}>Одна смена без учёта сроков</h2>
              </div>
              <figure className="pic pic--pass">
                <img src="/photos/pass.jpg" alt="Официант забирает блюда с раздачи" loading="lazy" />
              </figure>
            </div>
            <ol className="shift" style={{ listStyle: 'none', padding: 0 }}>
              <li>
                <span className="clock">12:40</span>
                <strong className="hd" style={{ fontSize: 18 }}>Гость заказал стейк, а лосось закончился</strong>
                <span>Официант пробил заказ, кухня узнала о нехватке только у плиты. Стоп-лист никто не обновил.</span>
              </li>
              <li>
                <span className="clock">16:00</span>
                <strong className="hd" style={{ fontSize: 18 }}>В холодильнике нашли просроченные сливки</strong>
                <span>Партию принимали неделю назад, срок помнил только тот, кто её ставил на полку.</span>
              </li>
              <li>
                <span className="clock">23:00</span>
                <strong className="hd" style={{ fontSize: 18 }}>Списание, о котором никто не знал заранее</strong>
                <span>Сумму видят в конце смены, когда продукт уже не продать и не переработать.</span>
              </li>
            </ol>
          </div>
        </section>

        <section className="sec" id="features" aria-labelledby="f-h">
          <div className="w">
            <span className="kick">Возможности</span>
            <h2 className="h2" id="f-h" style={{ marginTop: 10 }}>Что Остаток делает на кухне каждый день</h2>

            <div className="feat">
              <div className="feat__text">
                <h3>Партии и сроки</h3>
                <p>Каждая поставка — отдельная партия со своим сроком. При продаже блюда продукт списывается сначала из той партии, что истекает раньше.</p>
              </div>
              <div className="frag">
                <div className="g-colhead" style={cols('minmax(0, 1fr) auto auto')}>
                  <span>Лосось охлаждённый</span><span className="r">Остаток</span><span className="r">Срок</span>
                </div>
                <ul className="g-rows">
                  <li style={cols('minmax(0, 1fr) auto auto')}>
                    <div style={stack(2)}>
                      <span className="g-name">Партия П-0412</span>
                      <span className="small" style={{ color: 'var(--brand)', fontWeight: 600 }}>списывается первой</span>
                    </div>
                    <span className="num hd" style={{ fontWeight: 600 }}>0,7 кг</span>
                    <span className="g-lbl g-lbl--red" role="img" aria-label="Годен до 23:52, осталось 5 часов">
                      <span className="g-lbl__d" aria-hidden="true">ПТ</span>
                      <span className="g-lbl__b" aria-hidden="true"><span>до 23:52</span><span className="g-lbl__left">5 ч</span></span>
                    </span>
                  </li>
                  <li style={cols('minmax(0, 1fr) auto auto')}>
                    <div style={stack(2)}>
                      <span className="g-name">Партия П-0422</span>
                      <span className="muted small">принята сегодня</span>
                    </div>
                    <span className="num hd" style={{ fontWeight: 600 }}>3 кг</span>
                    <span className="g-lbl" role="img" aria-label="Годен до 12.10, осталось 3 дня">
                      <span className="g-lbl__d" aria-hidden="true">ПН</span>
                      <span className="g-lbl__b" aria-hidden="true"><span>до 12.10</span><span className="g-lbl__left">3 дня</span></span>
                    </span>
                  </li>
                </ul>
              </div>
            </div>

            <div className="feat feat--rev">
              <div className="feat__text">
                <h3>Стоп-лист без ручной работы</h3>
                <p>Блюдо уходит в стоп, когда годного продукта не хватает хотя бы на одну порцию, и возвращается в продажу после поставки. У каждого стопа есть причина.</p>
              </div>
              <div className="frag">
                <ul className="g-rows">
                  <StopRow name="Филе миньон" why="Вырезки 150 г, на порцию 220 г · с 18:32" tag="Стоп" kind="red" />
                  <StopRow name="Пицца «Маргарита»" why="Моцарелла просрочена · с 15:40" tag="Стоп" kind="red" />
                  <StopRow name="Тартар из говядины" why="1 порция · стоп примерно через 2 ч" tag="Заканчивается" kind="amber" />
                </ul>
              </div>
            </div>

            <div className="feat">
              <div className="feat__text">
                <h3>Горячие предложения</h3>
                <p>Список блюд, которые стоит продавать активнее сегодня: из каких продуктов, сколько у них осталось срока и сколько рублей уйдёт в списание, если их не продать.</p>
              </div>
              <div className="frag">
                <ul className="g-rows">
                  <HotRow photo="dish-salmon" name="Стейк из лосося" tag="лосось, осталось 5 ч" urgent loss="1 295 ₽" portions="3 порции" />
                  <HotRow photo="dish-caesar" name="Цезарь с креветками" tag="креветки, осталось 15 ч" loss="1 260 ₽" portions="6 порций" />
                  <HotRow photo="dish-bowl" name="Боул с лососем и авокадо" tag="авокадо, осталось 4 ч" urgent loss="480 ₽" portions="8 порций" />
                </ul>
              </div>
            </div>

            <div className="feat feat--rev">
              <div className="feat__text">
                <h3>Закупки и аналитика</h3>
                <p>Что пора заказать и почему, сколько ушло в списание за неделю или месяц и какие продукты заканчиваются чаще всего.</p>
              </div>
              <div className="frag">
                <ul className="g-rows">
                  <StopRow name="Креветки тигровые · 4 кг" why="Расход 3 кг в день, остатка хватит до завтра 14:00" tag="До завтра" kind="amber" />
                  <StopRow name="Говяжья вырезка · 3 кг" why="Расход 1,4 кг в день, осталось 150 г" tag="Заказать сегодня" kind="red" />
                </ul>
                <div style={{ padding: '16px 20px 20px', borderTop: '1px solid var(--line)' }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: 12 }}>
                    <span className="hd" style={{ fontWeight: 600 }}>Списания по дням</span>
                    <span className="num hd" style={{ fontWeight: 700 }}>10 853 ₽ за 14 дней</span>
                  </div>
                  <div aria-hidden="true" style={{ display: 'flex', alignItems: 'flex-end', gap: 6, height: 96, borderBottom: '1px solid var(--h)' }}>
                    {WRITE_OFFS.map((value, i) => (
                      <span
                        key={i}
                        style={{
                          flex: 1,
                          borderRadius: '3px 3px 0 0',
                          background: i === WRITE_OFFS.length - 1 ? 'var(--red)' : 'var(--brand)',
                          height: Math.round(Math.max((value / 2100) * 96, value ? 3 : 0)),
                        }}
                      />
                    ))}
                  </div>
                </div>
              </div>
            </div>
          </div>
        </section>

        <section className="sec sec--panel" id="why" aria-labelledby="w-h">
          <div className="w">
            <span className="kick">Почему мы</span>
            <h2 className="h2" id="w-h" style={{ marginTop: 10 }}>Чем Остаток отличается от таблицы и обычного складского учёта</h2>
            <ul className="why" style={{ listStyle: 'none', padding: 0 }}>
              <li><strong>Считаем по партиям, а не по общему остатку</strong><span>Десять килограммов лосося на складе ничего не значат, если семь из них истекают сегодня. Остаток знает срок каждой партии и списывает сначала ту, что истекает раньше.</span></li>
              <li><strong>Объясняем каждое решение</strong><span>Почему блюдо в стопе, почему его стоит продать первым, почему пора заказать продукт — причина написана рядом, словами и цифрами.</span></li>
              <li><strong>Предупреждаем до потери, а не после</strong><span>Сигнал приходит, когда у партии остались сутки, а не когда её уже списали. Для блюда видно, во сколько оно уйдёт в стоп, если ничего не привезут.</span></li>
              <li><strong>Экран обновляется сам</strong><span>Заказ, поставка, списание — остатки и стоп-лист меняются у всех сотрудников сразу, страницу перезагружать не нужно.</span></li>
              <li><strong>Поставку не нужно перепечатывать</strong><span>Файл от поставщика в Excel или CSV загружается целиком. Каждая строка проверяется до сохранения.</span></li>
              <li><strong>У каждого сотрудника свой экран</strong><span>Повар принимает заказы, кладовщик — поставки, управляющий видит закупки и аналитику. Кто что сделал, записано в журнале.</span></li>
            </ul>
          </div>
        </section>

        <section className="sec sec--dark" id="rollout" aria-labelledby="r-h">
          <div className="w">
            <span className="kick">Внедрение</span>
            <h2 className="h2" id="r-h" style={{ marginTop: 10, color: '#fff' }}>Запускаем вместе с вашей командой</h2>
            <ol className="steps" style={{ listStyle: 'none', padding: 0 }}>
              <li><b className="n">1</b><h3>Загружаем справочник продуктов и техкарты</h3><span className="muted">Из вашей таблицы или документов — переносим сами.</span></li>
              <li><b className="n">2</b><h3>Подключаем кассу по API или настраиваем ручной ввод</h3><span className="muted">Заказы и поставки начинают приходить в Остаток.</span></li>
              <li><b className="n">3</b><h3>Первая смена вместе с вашей командой</h3><span className="muted">Показываем повару, кладовщику и управляющему их экраны.</span></li>
              <li><b className="n">4</b><h3>Отчёт по итогам первой недели</h3><span className="muted">Что списали, что уходило в стоп и что стоит поменять в закупках.</span></li>
            </ol>
            <div className="need">
              <div>
                <h3 style={{ fontSize: 22 }}>Что нужно от ресторана</h3>
                <p className="muted" style={{ marginTop: 8 }}>Стоимость рассчитываем под число точек.</p>
              </div>
              <ul style={{ listStyle: 'none', padding: 0 }}>
                <li><strong>Список продуктов</strong><span>с единицами и сроками хранения, если они у вас записаны</span></li>
                <li><strong>Техкарты блюд</strong><span>сколько каждого продукта идёт в порцию</span></li>
                <li><strong>Ответственный сотрудник</strong><span>управляющий или шеф, который примет систему</span></li>
              </ul>
            </div>
          </div>
        </section>

        <section className="sec sec--panel" id="integrations" aria-labelledby="i-h">
          <div className="w">
            <span className="kick">Интеграции и доступ</span>
            <h2 className="h2" id="i-h" style={{ marginTop: 10 }}>Данные приходят сами, работать можно с любого устройства</h2>
            <div className="intg">
              <div className="api">
                <h3>Открытый API для кассовой системы</h3>
                <p style={{ margin: 0 }}>Поставки и заказы приходят автоматически. Если готовой связки с вашей кассой нет, подключим через API или настроим ручной ввод.</p>
                <pre>{`POST /api/orders
{
  "place": "Стол 3",
  "items": [
    { "dishId": "5f0c…", "quantity": 1 },
    { "dishId": "a81e…", "quantity": 2 }
  ]
}
→ 201 · заказ № 148, в ответе — из каких партий списано
→ 409 · заказ не создан: какого продукта сколько не хватило`}</pre>
              </div>
              <ul className="chan" style={{ listStyle: 'none', padding: 0 }}>
                <li>
                  <h3>Кабинет в браузере</h3><span className="muted small">компьютер, планшет</span>
                  <span className="muted" style={{ gridColumn: '1 / -1' }}>Всё, что на экранах выше: остатки, стоп-лист, закупки, аналитика.</span>
                </li>
                <li>
                  <h3>Приложение для Android</h3><span className="muted small">повар, кладовщик</span>
                  <span className="muted" style={{ gridColumn: '1 / -1' }}>Приёмка поставки и стоп-лист одной рукой у камеры.</span>
                </li>
                <li>
                  <h3>Telegram-бот</h3><span className="muted small">уведомления и управление</span>
                  <span className="bubble">В стоп: филе миньон. Вырезки 150 г, на порцию нужно 220 г. <b>18:32</b></span>
                </li>
              </ul>
            </div>
          </div>
        </section>

        <section className="sec" aria-labelledby="t-h" style={{ paddingBlock: 88 }}>
          <div className="w">
            <h2 className="h2" id="t-h">Надёжность</h2>
            <ul className="facts" style={{ listStyle: 'none', padding: 0 }}>
              <li><strong>Каждая операция в журнале</strong><span>Приёмка, продажа, списание — со временем, сотрудником и партией.</span></li>
              <li><strong>Два одновременных заказа не спишут один остаток</strong><span>Если продукта хватает на одну порцию, второй заказ получит отказ с причиной.</span></li>
              <li><strong>Вход только для сотрудников ресторана</strong><span>У каждого свой логин и доступ к своим разделам.</span></li>
            </ul>
          </div>
        </section>

        <section className="sec sec--panel" id="faq" aria-labelledby="q-h">
          <div className="w">
            <span className="kick">Вопросы</span>
            <h2 className="h2" id="q-h" style={{ marginTop: 10 }}>Частые вопросы</h2>
            <div className="faq">
              <details open><summary>Что нужно для старта?</summary><p>Список продуктов, техкарты блюд и сотрудник, который будет отвечать за систему. Справочники мы загружаем сами.</p></details>
              <details><summary>Можно ли без кассовой системы?</summary><p>Да. Заказы можно вводить в кабинете или с телефона, поставки — вручную или файлом от поставщика.</p></details>
              <details><summary>Что будет, если пропал интернет?</summary><p>Для работы нужен интернет. Если связь пропала, заказ или приёмка не отправятся, и Остаток сразу об этом скажет — действие не потеряется незаметно. Когда связь вернётся, его нужно повторить.</p></details>
              <details><summary>Как учитываются заготовки?</summary><p>Заготовка — такая же партия со своим сроком хранения. Её можно добавить в техкарту блюда наравне с продуктами.</p></details>
              <details><summary>Кто вводит поставки?</summary><p>Обычно кладовщик или повар с телефона при приёмке. Можно загрузить файл от поставщика или получать поставки через API.</p></details>
              <details><summary>Сколько времени занимает запуск?</summary><p>Зависит от числа блюд и точек. Сроки называем после того, как увидим ваше меню и техкарты.</p></details>
            </div>
          </div>
        </section>

        <section className="sec sec--dark" id="request" aria-labelledby="req-h">
          <div className="w req">
            <div style={stack(16)}>
              <h2 className="h2" id="req-h" style={{ color: '#fff' }}>Покажем на вашем меню</h2>
              <p className="lead" style={{ color: '#d7ecc6' }}>Пришлите заявку — подготовим демо с вашими блюдами и покажем, как Остаток встанет в работу кухни.</p>
              <p className="muted">Стоимость рассчитываем под число точек.</p>
              <p>Хотите посмотреть сами? <a className="lnk" href="/app" onClick={(e) => { e.preventDefault(); void openDemo(); }}>Открыть демо-ресторан</a></p>
              <figure className="pic pic--hall"><img src="/photos/hall.jpg" alt="Зал ресторана вечером" loading="lazy" /></figure>
            </div>
            <RequestForm />
          </div>
        </section>
      </main>

      <footer className="w" style={{ paddingTop: 56 }}>
        <div className="foot-top">
          <div>
            <h2 style={{ fontSize: 28, fontWeight: 800, letterSpacing: '-.02em', color: 'var(--h)', margin: 0 }}>Остаток под рукой</h2>
            <p className="muted" style={{ marginTop: 8 }}>Приложение для повара и кладовщика, бот — для уведомлений о сроках и стоп-листе.</p>
          </div>
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
            <a className="g-btn" href="https://github.com/3xfap2/hadashadfsj/releases/latest/download/ostatok.apk">Скачать приложение для Android</a>
            <a className="g-btn" href="#">Открыть Telegram-бота</a>
          </div>
        </div>
        <div className="foot-bot">
          <span className="g-brand" style={{ fontSize: 18 }}>Остаток</span>
          <span>Почта: <span className="num hd" style={{ userSelect: 'all' }}>hello@ostatok.example</span></span>
          <span>Телефон: <span className="num hd" style={{ userSelect: 'all' }}>+7 000 000-00-00</span></span>
        </div>
      </footer>

      <LandingChat />
    </div>
  );
}

function StopRow({ name, why, tag, kind }: { name: string; why: string; tag: string; kind: 'red' | 'amber' }) {
  return (
    <li style={cols('minmax(0, 1fr) auto')}>
      <div style={stack(2)}>
        <span className="g-name">{name}</span>
        <span className="muted small">{why}</span>
      </div>
      <span className={`g-tag g-tag--${kind}`}>{tag}</span>
    </li>
  );
}

function HotRow(props: { photo: string; name: string; tag: string; urgent?: boolean; loss: string; portions: string }) {
  return (
    <li style={cols('64px minmax(0, 1fr) auto')}>
      <span className="g-photo"><img src={`/photos/${props.photo}.jpg`} alt={props.name} loading="lazy" /></span>
      <div style={stack(4)}>
        <span className="g-name">{props.name}</span>
        {props.urgent
          ? <span className="g-tag g-tag--red" style={{ alignSelf: 'flex-start' }}>{props.tag}</span>
          : <span className="g-soon">{props.tag}</span>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end' }}>
        <b className="num hd">{props.loss}</b>
        <span className="muted small num">{props.portions}</span>
      </div>
    </li>
  );
}

const PHONE = /^\+?[\d\s()-]{10,}$/;
const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

function RequestForm() {
  const [values, setValues] = useState({ name: '', organization: '', contact: '', points: '1', comment: '' });
  const [tried, setTried] = useState(false);
  const [state, setState] = useState<'idle' | 'sending' | 'done' | 'failed'>('idle');

  const set = (key: keyof typeof values) => (e: { target: { value: string } }) => setValues((v) => ({ ...v, [key]: e.target.value }));
  const bad = {
    name: tried && !values.name.trim(),
    organization: tried && !values.organization.trim(),
    contact: tried && !(PHONE.test(values.contact.trim()) || EMAIL.test(values.contact.trim())),
  };
  const field = (invalid: boolean) => `g-field${invalid ? ' g-field--error' : ''}`;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    const contact = values.contact.trim();
    if (!values.name.trim() || !values.organization.trim() || !(PHONE.test(contact) || EMAIL.test(contact))) return;
    setState('sending');
    const ok = await fetch('/api/leads', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values) })
      .then((r) => r.ok)
      .catch(() => false);
    setState(ok ? 'done' : 'failed');
  };

  if (state === 'done') {
    return (
      <div className="form">
        <div className="g-note g-note--ok" role="status">
          <h3>Заявка принята, свяжемся в течение рабочего дня</h3>
          <span>Мы напишем или позвоним по контакту, который вы указали.</span>
        </div>
      </div>
    );
  }

  return (
    <form className="form" onSubmit={submit} noValidate>
      {state === 'failed' && (
        <div className="g-note g-note--err" role="alert">
          <h3>Заявка не отправлена</h3>
          <span>Проверьте соединение и нажмите «Запросить демо» ещё раз.</span>
        </div>
      )}
      <div className="two">
        <div className={field(bad.name)}>
          <label htmlFor="r-name">Имя</label>
          <input className="g-input" id="r-name" autoComplete="name" value={values.name} onChange={set('name')} aria-invalid={bad.name} aria-describedby={bad.name ? 'r-name-e' : undefined} />
          {bad.name && <span className="g-error-text" id="r-name-e">Как к вам обращаться?</span>}
        </div>
        <div className={field(bad.organization)}>
          <label htmlFor="r-org">Ресторан или сеть</label>
          <input className="g-input" id="r-org" autoComplete="organization" value={values.organization} onChange={set('organization')} aria-invalid={bad.organization} aria-describedby={bad.organization ? 'r-org-e' : undefined} />
          {bad.organization && <span className="g-error-text" id="r-org-e">Укажите название ресторана или сети</span>}
        </div>
      </div>
      <div className="two">
        <div className={field(bad.contact)}>
          <label htmlFor="r-contact">Телефон или почта</label>
          <input className="g-input" id="r-contact" value={values.contact} onChange={set('contact')} aria-invalid={bad.contact} aria-describedby={bad.contact ? 'r-contact-e' : undefined} />
          {bad.contact && <span className="g-error-text" id="r-contact-e">Укажите телефон, например +7 900 000-00-00, или почту</span>}
        </div>
        <div className="g-field">
          <label htmlFor="r-points">Число точек</label>
          <select className="g-input" id="r-points" value={values.points} onChange={set('points')}>
            <option>1</option><option>2–5</option><option>6–20</option><option>Больше 20</option>
          </select>
        </div>
      </div>
      <div className="g-field">
        <label htmlFor="r-comment">Комментарий <span className="muted" style={{ fontWeight: 400 }}>— необязательно</span></label>
        <textarea className="g-input" id="r-comment" placeholder="Например: есть касса, хотим подключить по API" value={values.comment} onChange={set('comment')} />
      </div>
      <button className="g-btn g-btn--primary g-btn--block" type="submit" disabled={state === 'sending'} style={{ height: 52, fontSize: 17 }}>
        {state === 'sending' ? 'Отправляем…' : 'Запросить демо'}
      </button>
      <span className="muted small">Нажимая кнопку, вы соглашаетесь, что мы свяжемся с вами по указанному контакту.</span>
    </form>
  );
}
