namespace Ostatok.Api.Domain;

public enum Unit { Kg, L, Pcs }

/// <summary>Запись, принадлежащая одному ресторану. Чужие записи запрос не видит: фильтр стоит в AppDbContext.</summary>
public interface IOwned
{
    Guid RestaurantId { get; set; }
}

/// <summary>Ресторан — отдельное рабочее пространство со своим складом, меню и сотрудниками.</summary>
public class Restaurant
{
    /// <summary>Демо-ресторан с готовыми данными; он же — «мы», кому приходят заявки и вопросы с лендинга.</summary>
    public static readonly Guid DemoId = new("0d3a0c1e-0000-4000-8000-000000000001");

    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string ShiftStart { get; set; } = "12:00";
    public string ShiftEnd { get; set; } = "24:00";
    public int Tables { get; set; } = 12;
    public bool HasBar { get; set; } = true;
    /// <summary>Номер последнего заказа: у каждого ресторана своя нумерация с единицы.</summary>
    public int LastOrderNumber { get; set; }
    /// <summary>Счётчики номеров заготовок («З-0001») и проверочных партий («Т-0001»).</summary>
    public int LastPrepNumber { get; set; }
    public int LastCheckNumber { get; set; }
    /// <summary>Номер последней инвентаризации.</summary>
    public int LastInventoryNumber { get; set; }
    /// <summary>
    /// Логин управляющего, который добавил этот ресторан себе сам. Пусто у ресторанов, открытых командой продукта:
    /// там владелец — управляющий из сотрудников ресторана.
    /// </summary>
    public string? OwnerLogin { get; set; }
    /// <summary>Когда команда продукта отключила ресторан; пусто — ресторан работает.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>Места, откуда приходят заказы: столы и бар.</summary>
    public IEnumerable<string> Places() =>
        Enumerable.Range(1, Tables).Select(n => $"Стол {n}").Concat(HasBar ? ["Бар"] : []);
}

public class Product : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Unit Unit { get; set; }
    /// <summary>Нормативный срок хранения; подставляется, если поставка не указала свой.</summary>
    public int ShelfLifeHours { get; set; }
    /// <summary>Сколько продукт хранится после вскрытия упаковки; пусто, если вскрытие срок не меняет.</summary>
    public int? OpenedShelfLifeHours { get; set; }
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

public class Supply : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public required string Number { get; set; }
    public string? Supplier { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Batch> Batches { get; set; } = [];
}

public enum IncomingStatus { Pending, Accepted, Rejected }

/// <summary>
/// Поставка, которую прислала внешняя система. На склад она не попадает, пока сотрудник не подтвердит её в кабинете:
/// партии и сроки годности появляются в момент приёмки.
/// </summary>
public class IncomingSupply : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public required string Number { get; set; }
    public string? Supplier { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>Дата создания поставки во внешней системе: от неё считается срок годности. Пусто у поставок, полученных до появления поля.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
    public IncomingStatus Status { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>Логин сотрудника, который принял или отклонил поставку.</summary>
    public string? DecidedBy { get; set; }
    public List<IncomingItem> Items { get; set; } = [];
}

public class IncomingItem
{
    public long Id { get; set; }
    public Guid IncomingSupplyId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public int? ShelfLifeHours { get; set; }
}

/// <summary>Партия: один продукт из одной поставки со своим сроком годности.</summary>
public class Batch : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public Guid SupplyId { get; set; }
    public Supply? Supply { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
    public decimal Remaining { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>
    /// Закупочная цена за единицу на момент приёмки. По ней считаются суммы поставки, списаний и недостач:
    /// новая цена в справочнике не переоценивает то, что уже куплено.
    /// </summary>
    public decimal UnitCost { get; set; }
    /// <summary>Когда упаковку вскрыли; с этого момента срок партии считается по сроку после вскрытия.</summary>
    public DateTimeOffset? OpenedAt { get; set; }
}

public class Dish : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public string? PhotoUrl { get; set; }
    /// <summary>Раздел меню: «Супы», «Салаты» и так далее.</summary>
    public string? Category { get; set; }
    /// <summary>Блюдо снято с продажи вручную и не продаётся, даже если продуктов хватает.</summary>
    public bool ManuallyStopped { get; set; }
    /// <summary>Менеджер закрепил блюдо первым в горячих предложениях.</summary>
    public bool HotPinned { get; set; }
    /// <summary>Менеджер убрал блюдо из горячих предложений.</summary>
    public bool HotHidden { get; set; }
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
public class StopLog : IOwned
{
    public Guid RestaurantId { get; set; }
    public long Id { get; set; }
    public Guid DishId { get; set; }
    public Dish? Dish { get; set; }
    public Guid ProductId { get; set; }
    public StopReason Reason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>Правила горячих предложений, которые настроил менеджер; строка одна.</summary>
public class HotSettings : IOwned
{
    public Guid RestaurantId { get; set; }
    public double HorizonHours { get; set; }
    public int UrgencyWeight { get; set; }
    public bool ShareAcrossDishes { get; set; }
    public int MaxDiscountPercent { get; set; }
}

public enum AlertKind { BatchExpiring, BatchExpired, DishStopped, DishBack, SupplyWaiting }

/// <summary>Сигнал для сотрудников. Прочитан он или нет, у каждого сотрудника своё: см. <see cref="User.LastReadAlertId"/>.</summary>
public class Alert : IOwned
{
    public Guid RestaurantId { get; set; }
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public AlertKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Detail { get; set; }
    /// <summary>Срок годности партии, о которой сигнал, — чтобы экран сам посчитал, сколько осталось.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? DishId { get; set; }
}

public class RecipeItem
{
    public Guid DishId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Quantity { get; set; }
}

public enum OrderStatus { Accepted, Rejected }

public class Order : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public OrderStatus Status { get; set; }
    /// <summary>Откуда заказ: стол или бар. Пусто, если заказ пришёл без места (например, из кассы).</summary>
    public string Place { get; set; } = "";
    /// <summary>Когда стол закрыли (гость расплатился); пусто, пока заказ открыт.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    /// <summary>Когда кухня отдала заказ; пусто, пока он в очереди у поваров.</summary>
    public DateTimeOffset? ReadyAt { get; set; }
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
    /// <summary>Цена блюда на момент заказа: сумма заказа не меняется, если блюдо потом подорожает.</summary>
    public decimal Price { get; set; }
    /// <summary>Скидка по горячему предложению, действовавшая в момент заказа; 0 — без скидки.</summary>
    public int DiscountPercent { get; set; }
    /// <summary>На сколько порций действует скидка: не больше, чем нужно продать, чтобы истекающий продукт не пропал.</summary>
    public int DiscountQuantity { get; set; }
}

public class OrderShortage
{
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Required { get; set; }
    public decimal Available { get; set; }
}

/// <summary>
/// PrepUse — сырьё ушло в заготовку, PrepMade — заготовка поступила на склад.
/// Shortage и Surplus — недостача и излишек, найденные при инвентаризации.
/// Opened — упаковку вскрыли: остаток не меняется, запись нужна, чтобы в журнале было видно, когда и кем сокращён срок.
/// </summary>
public enum MovementType { Supply, Order, WriteOff, PrepUse, PrepMade, Shortage, Surplus, Opened }

public class Movement : IOwned
{
    public Guid RestaurantId { get; set; }
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

/// <summary>Инвентаризация как документ: кто и когда пересчитал склад и что при этом нашлось.</summary>
public class Inventory : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public int Number { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>Логин сотрудника, который провёл пересчёт.</summary>
    public string Actor { get; set; } = "";
    /// <summary>Недостача и излишек в рублях по закупочным ценам на момент пересчёта.</summary>
    public decimal Shortage { get; set; }
    public decimal Surplus { get; set; }
    public List<InventoryItem> Items { get; set; } = [];
}

/// <summary>Строка инвентаризации: пересчитанный продукт, в том числе без расхождения.</summary>
public class InventoryItem
{
    public long Id { get; set; }
    public Guid InventoryId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Accounted { get; set; }
    public decimal Actual { get; set; }
    public decimal Difference { get; set; }
    /// <summary>Расхождение в рублях: недостача — с минусом.</summary>
    public decimal Value { get; set; }
}

public enum LeadStatus { New, Contacted, Demo, Won, Lost }

public class Lead
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Когда заявку последний раз меняли в CRM.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    public LeadStatus Status { get; set; }
    /// <summary>Кто из команды ведёт заявку.</summary>
    public string? Assignee { get; set; }
    /// <summary>Ресторан, открытый по заявке.</summary>
    public Guid? RestaurantId { get; set; }
    public required string Name { get; set; }
    public required string Organization { get; set; }
    public required string Contact { get; set; }
    public string Points { get; set; } = "";
    public string? Comment { get; set; }
}

/// <summary>Заметка команды продукта о заявке или о ресторане — ровно об одном из двух.</summary>
public class CrmNote
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Author { get; set; }
    public required string Text { get; set; }
    public long? LeadId { get; set; }
    public Guid? RestaurantId { get; set; }
}

/// <summary>Задача-напоминание команды продукта; может относиться к заявке или к ресторану.</summary>
public class CrmTask
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public long? LeadId { get; set; }
    public Guid? RestaurantId { get; set; }
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
    /// <summary>Заявка, к которой относится диалог.</summary>
    public long? LeadId { get; set; }
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

/// <summary>Чат Telegram, в который бот поддержки присылает заявки и вопросы с лендинга.</summary>
public class TelegramOperator
{
    public long ChatId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

public enum Role { Manager, Chef, Cook, Storekeeper, Waiter }

public class User : IOwned
{
    public Guid RestaurantId { get; set; }
    public Guid Id { get; set; }
    public required string Login { get; set; }
    public required string PasswordHash { get; set; }
    /// <summary>Должность, которая показывается в шапке кабинета.</summary>
    public string Title { get; set; } = "";
    public Role Role { get; set; }
    /// <summary>
    /// Растёт при смене роли или пароля. Токен помнит значение на момент входа: если оно устарело,
    /// сотрудника просят войти заново.
    /// </summary>
    public int TokenVersion { get; set; }
    /// <summary>Номер последнего сигнала, который сотрудник прочитал: всё, что новее, для него непрочитано.</summary>
    public long LastReadAlertId { get; set; }
    /// <summary>Номер последнего прочитанного сообщения владельца: всё, что новее, для сотрудника непрочитано.</summary>
    public long LastReadMessageId { get; set; }
}

/// <summary>
/// Сообщение в переписке владельца с сотрудником ресторана. Диалог — пара «ресторан + сотрудник»;
/// сообщение владельца без сотрудника адресовано всем сотрудникам ресторана.
/// </summary>
public class StaffMessage : IOwned
{
    public Guid RestaurantId { get; set; }
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>Написал владелец; иначе — сотрудник из поля Employee.</summary>
    public bool FromOwner { get; set; }
    /// <summary>Логин того, кто написал.</summary>
    public required string Author { get; set; }
    /// <summary>Логин сотрудника, с которым идёт диалог; пусто — сообщение владельца всем сотрудникам ресторана.</summary>
    public string? Employee { get; set; }
    public required string Text { get; set; }
    /// <summary>Когда владелец прочитал сообщение сотрудника. У сотрудника прочитанное считается по <see cref="User.LastReadMessageId"/>.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}
