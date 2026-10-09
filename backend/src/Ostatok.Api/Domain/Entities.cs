namespace Ostatok.Api.Domain;

public enum Unit { Kg, L, Pcs }

public class Product
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Unit Unit { get; set; }
    /// <summary>Нормативный срок хранения; подставляется, если поставка не указала свой.</summary>
    public int ShelfLifeHours { get; set; }
    /// <summary>Закупочная цена за единицу, ₽.</summary>
    public decimal Cost { get; set; }
}

public class Supply
{
    public Guid Id { get; set; }
    public required string Number { get; set; }
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
    public List<RecipeItem> Recipe { get; set; } = [];
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
}

public class OrderShortage
{
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal Required { get; set; }
    public decimal Available { get; set; }
}

public enum MovementType { Supply, Order, WriteOff }

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
