namespace Ostatok.Api.Domain;

public enum Unit { Kg, L, Pcs }

public class Product
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Unit Unit { get; set; }
    /// <summary>Нормативный срок хранения; подставляется, если поставка не указала свой.</summary>
    public int ShelfLifeHours { get; set; }
    /// <summary>Закупочная цена за единицу, ₽. У заготовки — стоимость её состава.</summary>
    public decimal Cost { get; set; }
    /// <summary>Состав заготовки на единицу продукта. Пусто у покупных продуктов.</summary>
    public List<PrepItem> PrepRecipe { get; set; } = [];
}

/// <summary>Строка состава заготовки: сколько продукта идёт на единицу заготовки.</summary>
public class PrepItem
{
    public Guid PrepId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
}

public class Supply
{
    public Guid Id { get; set; }
    public required string Number { get; set; }
    public string? Supplier { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Batch> Batches { get; set; } = [];
}

/// <summary>Партия: один продукт из одной поставки со своим сроком годности.</summary>
public class Batch
{
    public Guid Id { get; set; }
    public Guid SupplyId { get; set; }
    public Supply? Supply { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal Remaining { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class Dish
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public string? PhotoUrl { get; set; }
    /// <summary>Раздел меню: «Супы», «Салаты» и так далее.</summary>
    public string? Category { get; set; }
    /// <summary>Блюдо снято с продажи вручную и не продаётся, даже если продуктов хватает.</summary>
    public bool ManuallyStopped { get; set; }
    public List<RecipeItem> Recipe { get; set; } = [];
}

/// <summary>Загруженное фото блюда; хранится отдельно, чтобы не тянуть его вместе со списком блюд.</summary>
public class DishPhoto
{
    public Guid DishId { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
}

/// <summary>Интервал, когда блюдо было в стоп-листе; EndedAt пуст, пока стоп продолжается.</summary>
public class StopLog
{
    public long Id { get; set; }
    public Guid DishId { get; set; }
    public Dish? Dish { get; set; }
    public Guid ProductId { get; set; }
    public StopReason Reason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public enum AlertKind { BatchExpiring, BatchExpired, DishStopped, DishBack }

/// <summary>Сигнал для сотрудников; ReadAt пуст, пока сигнал не прочитан.</summary>
public class Alert
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public AlertKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Detail { get; set; }
    /// <summary>Срок годности партии, о которой сигнал, — чтобы экран сам посчитал, сколько осталось.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? DishId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public class RecipeItem
{
    public Guid DishId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
}

public enum OrderStatus { Accepted, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public OrderStatus Status { get; set; }
    /// <summary>Откуда заказ: стол или бар. Пусто, если заказ пришёл без места (например, из кассы).</summary>
    public string Place { get; set; } = "";
    /// <summary>Когда стол закрыли (гость расплатился); пусто, пока заказ открыт.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    public List<OrderItem> Items { get; set; } = [];
    /// <summary>Чего не хватило; заполнено только у отклонённого заказа.</summary>
    public List<OrderShortage> Shortages { get; set; } = [];
}

public class OrderItem
{
    public Guid OrderId { get; set; }
    public Guid DishId { get; set; }
    public Dish? Dish { get; set; }
    public int Quantity { get; set; }
    /// <summary>Скидка по горячему предложению, действовавшая в момент заказа; 0 — без скидки.</summary>
    public int DiscountPercent { get; set; }
}

public class OrderShortage
{
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Required { get; set; }
    public decimal Available { get; set; }
}

/// <summary>PrepUse — сырьё ушло в заготовку, PrepMade — заготовка поступила на склад.</summary>
public enum MovementType { Supply, Order, WriteOff, PrepUse, PrepMade }

public class Movement
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public MovementType Type { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid BatchId { get; set; }
    public decimal Quantity { get; set; }
    public Guid? OrderId { get; set; }
    public string? Note { get; set; }
    /// <summary>Логин сотрудника, который провёл операцию; «система» — для автосписания.</summary>
    public string Actor { get; set; } = "";
}

public class Lead
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Name { get; set; }
    public required string Organization { get; set; }
    public required string Contact { get; set; }
    public string Points { get; set; } = "";
    public string? Comment { get; set; }
}

/// <summary>Диалог посетителя лендинга с командой продукта.</summary>
public class Chat
{
    /// <summary>Случайный идентификатор; он же ключ посетителя к своему диалогу.</summary>
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public required string Name { get; set; }
    public required string Contact { get; set; }
    /// <summary>Сколько сообщений посетителя ещё не прочитано сотрудником.</summary>
    public int Unread { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage
{
    public long Id { get; set; }
    public Guid ChatId { get; set; }
    public DateTimeOffset At { get; set; }
    public bool FromStaff { get; set; }
    /// <summary>Логин сотрудника, который ответил; пусто у сообщений посетителя.</summary>
    public string Author { get; set; } = "";
    public required string Text { get; set; }
}

public enum Role { Manager, Chef, Cook, Storekeeper }

public class User
{
    public Guid Id { get; set; }
    public required string Login { get; set; }
    public required string PasswordHash { get; set; }
    /// <summary>Должность, которая показывается в шапке кабинета.</summary>
    public string Title { get; set; } = "";
    public Role Role { get; set; }
}
