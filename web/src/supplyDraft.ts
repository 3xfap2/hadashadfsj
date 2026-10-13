import { getLogin, getToken } from './api';

// Черновик поставки хранится в браузере: недописанная форма не пропадает при закрытии страницы.
// Он виден только на этом устройстве; на склад ничего не попадает, пока поставку не приняли.

export interface DraftLine {
  productId: string;
  quantity: string;
  life: string;
}

export interface SupplyDraft {
  number: string;
  /** Значение поля даты; пустая строка — «сейчас». */
  date: string;
  supplier: string;
  lines: DraftLine[];
}

/** Ресторан из токена (поле restaurant). Подпись не проверяется: он нужен только для ключа черновика. */
function restaurant(): string {
  try {
    const payload = getToken()!.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return String(JSON.parse(atob(payload)).restaurant ?? '');
  } catch {
    return '';
  }
}

// Черновик свой у каждого сотрудника в каждом ресторане: у владельца нескольких ресторанов логин один,
// а продукты из черновика одного ресторана в справочнике другого не найдутся.
const key = () => `ostatok.supply-draft.${getLogin()}.${restaurant()}`;

export function loadDraft(): SupplyDraft | null {
  try {
    const raw = localStorage.getItem(key());
    return raw ? (JSON.parse(raw) as SupplyDraft) : null;
  } catch {
    return null;
  }
}

export function saveDraft(draft: SupplyDraft) {
  try {
    localStorage.setItem(key(), JSON.stringify(draft));
  } catch {
    // Хранилище недоступно (приватный режим) — черновик останется только на открытой странице.
  }
}

export function clearDraft() {
  try {
    localStorage.removeItem(key());
  } catch {
    // Нечего удалять.
  }
}
