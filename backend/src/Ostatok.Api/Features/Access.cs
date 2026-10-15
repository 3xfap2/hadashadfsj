using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>
/// Кто что может. У каждой роли своя точка входа: официант работает только с заказами, кладовщик — со складом,
/// управляющий — со складом, аналитикой и сотрудниками. Заказ оформляет только официант: так видно, кто его принял.
/// Смотреть остатки и стоп-лист могут все — без этого не работает ни один экран.
/// </summary>
public static class Access
{
    public static readonly Role[] Orders = [Role.Waiter];
    /// <summary>Кухня: заготовки и ручной стоп блюда.</summary>
    public static readonly Role[] Kitchen = [Role.Manager, Role.Chef, Role.Cook];
    public static readonly Role[] Stock = [Role.Manager, Role.Chef, Role.Storekeeper];
    public static readonly Role[] Purchasing = [Role.Manager, Role.Chef, Role.Storekeeper];
    public static readonly Role[] Analytics = [Role.Manager, Role.Chef];
    public static readonly Role[] Catalog = [Role.Manager, Role.Chef];
    public static readonly Role[] Staff = [Role.Manager];
    public static readonly Role[] Leads = [Role.Manager];
    /// <summary>Зал: брони, посадка, завершение визита и уборка. Схему, настройки зала и закрытие столов меняет только управляющий.</summary>
    public static readonly Role[] Hall = [Role.Manager, Role.Chef, Role.Waiter];
    public static readonly Role[] HallAdmin = [Role.Manager];
    /// <summary>
    /// Кто видит закупочные цены и суммы по ним. Официанту и повару они для работы не нужны:
    /// сервер отдаёт им те же списки без этих цифр.
    /// </summary>
    public static readonly Role[] Costs = [Role.Manager, Role.Chef, Role.Storekeeper];

    public static bool SeesCosts(this System.Security.Claims.ClaimsPrincipal user) => Costs.Any(r => user.IsInRole(r.ToString()));

    /// <summary>Разделы кабинета, которые видит роль; по этому списку сайт строит меню.</summary>
    /// <param name="vendor">Сотрудник команды продукта: ему приходят заявки и вопросы с лендинга.</param>
    public static IEnumerable<string> Sections(Role role, bool vendor)
    {
        if (role != Role.Waiter) yield return "today";
        if (Orders.Contains(role)) yield return "orders";
        if (Hall.Contains(role)) yield return "hall";
        if (Stock.Contains(role)) yield return "stock";
        if (role != Role.Storekeeper) yield return "menu";
        if (Kitchen.Contains(role)) yield return "board";
        if (Kitchen.Contains(role)) yield return "kitchen";
        if (Purchasing.Contains(role)) yield return "purchasing";
        if (Analytics.Contains(role)) yield return "analytics";
        if (Catalog.Contains(role)) yield return "catalog";
        if (Staff.Contains(role)) yield return "staff";
        if (vendor && Leads.Contains(role)) yield return "support";
    }

    public static TBuilder RequireRoles<TBuilder>(this TBuilder builder, Role[] roles) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireRole(roles.Select(r => r.ToString())));

    /// <summary>Заявки и вопросы с лендинга адресованы команде продукта, а не каждому ресторану.</summary>
    public static TBuilder VendorOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            return IsVendor(context.HttpContext.User.Identity?.Name, services.GetRequiredService<Tenant>().Id, services.GetRequiredService<AuthOptions>())
                ? await next(context)
                : Results.Forbid();
        });

    /// <summary>
    /// Команда продукта — одна закрытая учётная запись. Открытые демо-записи заявок и переписки не видят:
    /// их пароль опубликован.
    /// </summary>
    public static bool IsVendor(string? login, Guid restaurant, AuthOptions options) =>
        restaurant == Restaurant.DemoId && options.SupportPassword != "" && login == options.SupportLogin;
}
