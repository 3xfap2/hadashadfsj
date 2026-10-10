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

const KEY = 'ostatok.supply-draft';

export function loadDraft(): SupplyDraft | null {
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? (JSON.parse(raw) as SupplyDraft) : null;
  } catch {
    return null;
  }
}

export function saveDraft(draft: SupplyDraft) {
  try {
    localStorage.setItem(KEY, JSON.stringify(draft));
  } catch {
    // Хранилище недоступно (приватный режим) — черновик останется только на открытой странице.
  }
}

export function clearDraft() {
  try {
    localStorage.removeItem(KEY);
  } catch {
    // Нечего удалять.
  }
}
