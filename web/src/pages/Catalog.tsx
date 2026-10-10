import { useEffect, useState, type ChangeEvent, type FormEvent } from 'react';
import { NavLink, useSearchParams } from 'react-router-dom';
import { send, sendFile, useApi, type DishRow, type ProductRow, type StopListRow, type Unit } from '../api';
import { money, plural, portions, shelfLife, unitName } from '../format';
import { Photo, PlusIcon } from '../ui';

type Note = { kind: 'ok' | 'err'; text: string } | null;

const UNITS: Unit[] = ['Kg', 'L', 'Pcs'];
const number = (text: string) => Number(String(text).replace(',', '.'));

export function CatalogTabs() {
  return (
    <nav className="g-seg" aria-label="Справочник">
      <NavLink to="/app/catalog" end>Продукты</NavLink>
      <NavLink to="/app/catalog/dishes">Блюда и техкарты</NavLink>
      <NavLink to="/app/stock/import">Загрузка поставки</NavLink>
    </nav>
  );
}

/** Текст ошибок из ответа сервера: поле errors при 400 или detail при 409. */
async function problemText(response: Response): Promise<string> {
  const problem = await response.json().catch(() => ({}));
  const messages = Object.values((problem.errors ?? {}) as Record<string, string[]>).flat();
  return messages.join(' ') || problem.detail || `Сервер ответил ошибкой ${response.status}.`;
}

function initials(name: string): string {
  const words = name.replace(/[«»"]/g, '').split(/\s+/).filter((w) => w.length > 2 && /^[А-ЯЁа-яёA-Za-z]/.test(w));
  return (words.length > 1 ? words[0][0] + words[1][0] : (words[0] ?? name).slice(0, 2)).toUpperCase();
}

// ---------- Продукты ----------

interface ProductForm {
  id: string | null;
  name: string;
  unit: Unit;
  life: string;
  /** Срок вводится в днях, если он длиннее пяти суток и кратен суткам, иначе в часах. */
  lifeInDays: boolean;
  cost: string;
}

const emptyProduct: ProductForm = { id: null, name: '', unit: 'Kg', life: '', lifeInDays: false, cost: '' };

function toForm(p: ProductRow): ProductForm {
  const inDays = p.shelfLifeHours > 120 && p.shelfLifeHours % 24 === 0;
  return { id: p.id, name: p.name, unit: p.unit, life: String(inDays ? p.shelfLifeHours / 24 : p.shelfLifeHours), lifeInDays: inDays, cost: String(p.cost).replace('.', ',') };
}

export function Products() {
  const products = useApi<ProductRow[]>('/products', 60_000);
  const dishes = useApi<DishRow[]>('/dishes', 60_000);
  const [form, setForm] = useState<ProductForm | null>(null);
  const [tried, setTried] = useState(false);
  const [note, setNote] = useState<Note>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!form && products.data?.length) setForm(toForm(products.data[0]));
  }, [form, products.data]);

  const error = products.error ?? dishes.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить справочник</h3><span>{error}</span></div>;
  if (!products.data || !dishes.data || !form) return <p className="muted">Загружаем справочник…</p>;

  const usedIn = form.id ? dishes.data.filter((d) => d.recipe.some((r) => r.productId === form.id)).length : 0;
  const bad = { name: tried && !form.name.trim(), life: tried && !(number(form.life) > 0), cost: tried && !(number(form.cost) > 0) };
  const pick = (next: ProductForm) => { setForm(next); setTried(false); setNote(null); };
  const field = (invalid: boolean) => `g-field${invalid ? ' g-field--error' : ''}`;

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    if (!form.name.trim() || !(number(form.life) > 0) || !(number(form.cost) > 0)) return;
    setBusy(true);
    const body = { name: form.name.trim(), unit: form.unit, shelfLifeHours: Math.round(number(form.life) * (form.lifeInDays ? 24 : 1)), cost: number(form.cost) };
    const response = await send(form.id ? 'PUT' : 'POST', form.id ? `/products/${form.id}` : '/products', body);
    if (response.ok) {
      const saved: ProductRow = await response.json();
      await products.reload();
      setForm(toForm(saved));
      setTried(false);
      setNote({ kind: 'ok', text: 'Продукт сохранён.' });
    } else {
      setNote({ kind: 'err', text: await problemText(response) });
    }
    setBusy(false);
  };

  const remove = async () => {
    if (!form.id) return;
    setBusy(true);
    const response = await send('DELETE', `/products/${form.id}`);
    if (response.ok) {
      await products.reload();
      setForm(null);
      setNote(null);
    } else {
      setNote({ kind: 'err', text: await problemText(response) });
    }
    setBusy(false);
  };

  return (
    <>
      <div className="g-title cat-title">
        <div className="stock-head"><h1>Справочники</h1><CatalogTabs /></div>
        <div className="end"><button className="g-btn" type="button" onClick={() => pick(emptyProduct)}><PlusIcon />Добавить продукт</button></div>
      </div>

      <div className="sup-row">
        <section className="sup-main g-panel cat-list" aria-label="Продукты">
          <div className="g-colhead pcols" aria-hidden="true">
            <span /><span>Продукт</span><span>Ед.</span><span className="s-hide r">Срок хранения</span><span className="s-hide r">Цена закупки</span>
          </div>
          <div>
            {products.data.map((p) => (
              <button key={p.id} className="prod-row pcols" type="button" aria-pressed={form.id === p.id} onClick={() => pick(toForm(p))}>
                <span className="g-photo prod-row__photo" role="img" aria-label={`Место для фото: ${p.name}`}><span aria-hidden="true">{initials(p.name)}</span></span>
                <span className="g-name">{p.name}</span>
                <span>{unitName(p.unit)}</span>
                <span className="s-hide num r">{shelfLife(p.shelfLifeHours)}</span>
                <span className="s-hide num r hd prod-row__price">{money(p.cost)}</span>
              </button>
            ))}
          </div>
        </section>

        <aside className="sup-side cat-side">
          <form className="g-panel" onSubmit={save} noValidate>
            <div className="g-panel__head">
              <h2 className="cat-form-title">{form.id ? form.name || 'Продукт' : 'Новый продукт'}</h2>
              {form.id && <span className="end">{usedIn > 0 ? `в ${plural(usedIn, 'блюде', 'блюдах', 'блюдах')}` : 'не используется'}</span>}
            </div>
            <div className="form-body">
              <div className={field(bad.name)}>
                <label htmlFor="p-name">Название</label>
                <input className="g-input" id="p-name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} aria-invalid={bad.name} />
                {bad.name && <span className="g-error-text">Укажите название продукта</span>}
              </div>
              <div className="g-field">
                <span className="lb" id="u-l">Единица</span>
                <div className="g-seg cat-units" role="group" aria-labelledby="u-l">
                  {UNITS.map((u) => <button key={u} type="button" aria-pressed={form.unit === u} onClick={() => setForm({ ...form, unit: u })}>{unitName(u)}</button>)}
                </div>
              </div>
              <div className={field(bad.life)}>
                <label htmlFor="p-life">Срок хранения</label>
                <div className="g-unit">
                  <input className="g-input num" id="p-life" inputMode="numeric" value={form.life} onChange={(e) => setForm({ ...form, life: e.target.value })} aria-invalid={bad.life} />
                  <span>{form.lifeInDays ? 'дней' : 'ч'}</span>
                </div>
                {bad.life && <span className="g-error-text">Укажите срок больше нуля, например 72</span>}
                <span className="g-hint">От приёмки партии. По нему в поставке подставится «годен до».</span>
              </div>
              <div className={field(bad.cost)}>
                <label htmlFor="p-price">Цена закупки за единицу</label>
                <div className="g-unit">
                  <input className="g-input num" id="p-price" inputMode="decimal" value={form.cost} onChange={(e) => setForm({ ...form, cost: e.target.value })} aria-invalid={bad.cost} aria-describedby={bad.cost ? 'p-price-e' : undefined} />
                  <span>₽ / {unitName(form.unit)}</span>
                </div>
                {bad.cost && <span className="g-error-text" id="p-price-e">Укажите цену больше нуля, например 980</span>}
              </div>
              {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
            </div>
            <div className="g-panel__foot cat-foot">
              {form.id ? <button className="g-btn cat-delete" type="button" disabled={busy} onClick={remove}>Удалить</button> : <span />}
              <button className="g-btn g-btn--primary" type="submit" disabled={busy}>Сохранить</button>
            </div>
          </form>
        </aside>
      </div>
    </>
  );
}

// ---------- Блюда и техкарты ----------

interface RecipeLine {
  productId: string;
  /** Количество в «поварских» единицах: граммы и миллилитры для кг и л, штуки для штук. */
  quantity: string;
}

interface DishForm {
  id: string | null;
  name: string;
  price: string;
  category: string;
  recipe: RecipeLine[];
}

const small = (unit: Unit) => (unit === 'Pcs' ? 1 : 1000);
const smallUnit = (unit: Unit) => (unit === 'Kg' ? 'г' : unit === 'L' ? 'мл' : 'шт');

function toDishForm(d: DishRow, products: ProductRow[]): DishForm {
  return {
    id: d.id,
    name: d.name,
    price: String(d.price),
    category: d.category ?? '',
    recipe: d.recipe.map((r) => {
      const unit = products.find((p) => p.id === r.productId)!.unit;
      return { productId: r.productId, quantity: String(Math.round(r.quantity * small(unit) * 1000) / 1000).replace('.', ',') };
    }),
  };
}

/** Уменьшает фото до 1000 px по длинной стороне: так оно быстро грузится и мало весит в базе. */
async function shrink(file: File): Promise<Blob> {
  const image = await createImageBitmap(file);
  const scale = Math.min(1, 1000 / Math.max(image.width, image.height));
  const canvas = Object.assign(document.createElement('canvas'), { width: Math.round(image.width * scale), height: Math.round(image.height * scale) });
  canvas.getContext('2d')!.drawImage(image, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) => canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('Фото не удалось обработать'))), 'image/jpeg', 0.85));
}

export function Dishes() {
  const dishes = useApi<DishRow[]>('/dishes', 60_000);
  const products = useApi<ProductRow[]>('/products', 300_000);
  const menu = useApi<StopListRow[]>('/stop-list');
  const [params, setParams] = useSearchParams();
  const [form, setForm] = useState<DishForm | null>(null);
  const [photo, setPhoto] = useState<{ file: File; preview: string } | null>(null);
  const [tried, setTried] = useState(false);
  const [note, setNote] = useState<Note>(null);
  const [busy, setBusy] = useState(false);

  // Открывается блюдо из адреса (ссылка «Изменить техкарту» из меню) или первое в списке.
  useEffect(() => {
    if (form || !dishes.data || !products.data) return;
    const wanted = dishes.data.find((d) => d.id === params.get('dish')) ?? dishes.data[0];
    if (wanted) setForm(toDishForm(wanted, products.data));
  }, [form, dishes.data, products.data, params]);

  const error = dishes.error ?? products.error ?? menu.error;
  if (error) return <div className="g-note g-note--err" role="alert"><h3>Не удалось загрузить блюда</h3><span>{error}</span></div>;
  if (!dishes.data || !products.data || !menu.data || !form) return <p className="muted">Загружаем блюда…</p>;

  const byId = (id: string) => products.data!.find((p) => p.id === id)!;
  const current = dishes.data.find((d) => d.id === form.id);
  const state = menu.data.find((d) => d.dishId === form.id);
  const categories = [...new Set(dishes.data.map((d) => d.category).filter((c): c is string => !!c))].sort((a, b) => a.localeCompare(b, 'ru'));
  const lineCost = (l: RecipeLine) => ((number(l.quantity) || 0) / small(byId(l.productId).unit)) * byId(l.productId).cost;
  const cost = form.recipe.reduce((sum, l) => sum + lineCost(l), 0);
  const price = number(form.price) || 0;
  const bad = {
    name: tried && !form.name.trim(),
    price: tried && !(price > 0),
    recipe: tried && (form.recipe.length === 0 || form.recipe.some((l) => !(number(l.quantity) > 0))),
  };

  const pick = (next: DishForm) => {
    setForm(next);
    setPhoto(null);
    setTried(false);
    setNote(null);
    setParams(next.id ? { dish: next.id } : {}, { replace: true });
  };

  const choosePhoto = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    if (file.size > 10_000_000) return setNote({ kind: 'err', text: 'Фото больше 10 МБ. Выберите файл поменьше.' });
    setPhoto({ file, preview: URL.createObjectURL(file) });
  };

  const save = async (e: FormEvent) => {
    e.preventDefault();
    setTried(true);
    if (!form.name.trim() || !(price > 0) || form.recipe.length === 0 || form.recipe.some((l) => !(number(l.quantity) > 0))) return;
    setBusy(true);
    try {
      const body = {
        name: form.name.trim(),
        price,
        category: form.category.trim() || null,
        recipe: form.recipe.map((l) => ({ productId: l.productId, quantity: number(l.quantity) / small(byId(l.productId).unit) })),
      };
      const response = await send(form.id ? 'PUT' : 'POST', form.id ? `/dishes/${form.id}` : '/dishes', body);
      if (!response.ok) return setNote({ kind: 'err', text: await problemText(response) });
      const saved: DishRow = await response.json();
      if (photo) {
        const uploaded = await sendFile('PUT', `/dishes/${saved.id}/photo`, await shrink(photo.file), 'image/jpeg');
        if (!uploaded.ok) setNote({ kind: 'err', text: `Блюдо сохранено, но фото не загрузилось. ${await problemText(uploaded)}` });
      }
      await Promise.all([dishes.reload(), menu.reload()]);
      setPhoto(null);
      setTried(false);
      setForm({ ...toDishForm(saved, products.data!) });
      setParams({ dish: saved.id }, { replace: true });
      if (!photo) setNote({ kind: 'ok', text: 'Блюдо сохранено.' });
      else setNote((n) => n ?? { kind: 'ok', text: 'Блюдо и фото сохранены.' });
    } catch (err) {
      setNote({ kind: 'err', text: (err as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const toggleStop = async () => {
    if (!current) return;
    setBusy(true);
    await send('POST', `/dishes/${current.id}/${current.manuallyStopped ? 'resume' : 'stop'}`);
    await Promise.all([dishes.reload(), menu.reload()]);
    setBusy(false);
  };

  const unused = products.data.filter((p) => !form.recipe.some((l) => l.productId === p.id));

  return (
    <>
      <div className="g-title cat-title">
        <div className="stock-head"><h1>Справочники</h1><CatalogTabs /></div>
        <div className="end">
          <button className="g-btn" type="button" onClick={() => pick({ id: null, name: '', price: '', category: '', recipe: [] })}><PlusIcon />Добавить блюдо</button>
        </div>
      </div>

      <div className="sup-row">
        <section className="dl-list" aria-label="Блюда">
          {dishes.data.map((d) => (
            <button key={d.id} className="dl" type="button" aria-pressed={form.id === d.id} onClick={() => pick(toDishForm(d, products.data!))}>
              <Photo url={d.photoUrl} name={d.name} />
              <span className="dl__text">
                <span className="g-name">{d.name}</span>
                <span className="muted small">{d.category ? `${d.category} · ` : ''}{plural(d.recipe.length, 'продукт', 'продукта', 'продуктов')}</span>
              </span>
              <span className="num r hd dl__price">{money(d.price)}</span>
            </button>
          ))}
        </section>

        <form className="sup-main g-panel" onSubmit={save} noValidate>
          <div className="g-panel__head">
            <h2 className="cat-form-title">{form.id ? form.name || 'Блюдо' : 'Новое блюдо'}</h2>
            {state && <span className="end">{state.status === 'Stop' ? (state.reason === 'Manual' ? 'Снято с меню' : 'Стоп') : state.status === 'Low' ? 'Заканчивается' : 'В продаже'}</span>}
          </div>
          <div className="dish-form">
            <div className="dish-form__photo">
              {photo || current?.photoUrl ? (
                <img className="dish-drop dish-drop--filled" src={photo?.preview ?? current!.photoUrl!} alt="" />
              ) : (
                <div className="dish-drop" role="img" aria-label="Место для фото блюда">
                  <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 7h4l2-3h6l2 3h4v13H3z" /><circle cx="12" cy="13" r="4" /></svg>
                  <span>Фото блюда<br />JPG или PNG до 10 МБ</span>
                </div>
              )}
              <label className="g-btn" htmlFor="dish-photo">Выбрать файл</label>
              <input className="sr" id="dish-photo" type="file" accept="image/jpeg,image/png,image/webp" onChange={choosePhoto} />
              {photo && <span className="muted small">Новое фото сохранится вместе с блюдом.</span>}
            </div>

            <div className="dish-form__fields">
              <div className={`g-field${bad.name ? ' g-field--error' : ''}`}>
                <label htmlFor="d-name">Название в меню</label>
                <input className="g-input" id="d-name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} aria-invalid={bad.name} />
                {bad.name && <span className="g-error-text">Укажите название блюда</span>}
              </div>
              <div className="dish-form__pair">
                <div className={`g-field${bad.price ? ' g-field--error' : ''}`}>
                  <label htmlFor="d-price">Цена</label>
                  <div className="g-unit"><input className="g-input num" id="d-price" inputMode="decimal" value={form.price} onChange={(e) => setForm({ ...form, price: e.target.value })} aria-invalid={bad.price} /><span>₽</span></div>
                  {bad.price && <span className="g-error-text">Укажите цену больше нуля</span>}
                </div>
                <div className="g-field">
                  <label htmlFor="d-cat">Раздел меню</label>
                  <input className="g-input" id="d-cat" list="d-cats" value={form.category} onChange={(e) => setForm({ ...form, category: e.target.value })} />
                  <datalist id="d-cats">{categories.map((c) => <option key={c} value={c} />)}</datalist>
                </div>
              </div>

              <h3 className="dish-form__tk-title">Техкарта на 1 порцию</h3>
              <div>
                <div className="tc tc-head" aria-hidden="true"><span>Продукт</span><span>Количество</span><span className="cost r">Себестоимость</span><span /></div>
                {form.recipe.map((l, i) => {
                  const product = byId(l.productId);
                  const update = (patch: Partial<RecipeLine>) => setForm({ ...form, recipe: form.recipe.map((x, j) => (j === i ? { ...x, ...patch } : x)) });
                  return (
                    <div className="tc" key={l.productId}>
                      <label className="sr" htmlFor={`tp${i}`}>Продукт</label>
                      <select className="g-input" id={`tp${i}`} value={l.productId} onChange={(e) => update({ productId: e.target.value })}>
                        {[product, ...unused].map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                      </select>
                      <div className="g-unit">
                        <label className="sr" htmlFor={`tq${i}`}>Количество</label>
                        <input className="g-input num" id={`tq${i}`} inputMode="decimal" value={l.quantity} onChange={(e) => update({ quantity: e.target.value })} />
                        <span>{smallUnit(product.unit)}</span>
                      </div>
                      <span className="cost num r hd">{money(lineCost(l))}</span>
                      <button className="del" type="button" aria-label={`Убрать ${product.name} из техкарты`} onClick={() => setForm({ ...form, recipe: form.recipe.filter((_, j) => j !== i) })}>
                        <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18" /></svg>
                      </button>
                    </div>
                  );
                })}
                {bad.recipe && <p className="g-error-text dish-form__tk-error">В техкарте должен быть хотя бы один продукт, у каждого — количество больше нуля.</p>}
              </div>
              <button className="g-btn dish-form__add" type="button" disabled={unused.length === 0} onClick={() => setForm({ ...form, recipe: [...form.recipe, { productId: unused[0].id, quantity: '' }] })}>
                <PlusIcon />Добавить продукт
              </button>

              <div className="facts-3">
                <div className="tile"><div>Себестоимость</div><div className="v num">{money(cost)}</div></div>
                <div className="tile"><div>Наценка</div><div className="v num">{cost > 0 && price > 0 ? `${Math.round(((price - cost) / cost) * 100)}%` : '—'}</div></div>
                <div className="tile"><div>Хватит сейчас</div><div className="v num">{state ? portions(state.portions) : '—'}</div></div>
              </div>
              {note && <div className={`g-note g-note--${note.kind}`} role={note.kind === 'err' ? 'alert' : 'status'}><span>{note.text}</span></div>}
            </div>
          </div>
          <div className="g-panel__foot cat-foot">
            {current ? <button className="g-btn" type="button" disabled={busy} onClick={toggleStop}>{current.manuallyStopped ? 'Вернуть в меню' : 'Снять с меню'}</button> : <span />}
            <button className="g-btn g-btn--primary" type="submit" disabled={busy}>{busy ? 'Сохраняем…' : 'Сохранить блюдо'}</button>
          </div>
        </form>
      </div>
    </>
  );
}
