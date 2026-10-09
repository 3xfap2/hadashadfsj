using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

/// <summary>
/// Кто что может. Смотреть остатки, стоп-лист и заказы могут все сотрудники — без этого не работает экран смены;
/// менять данные и открывать разделы может только роль, для которой это рабочая задача.
/// </summary>
public static class Access
{
    public static readonly Role[] Orders = [Role.Manager, Role.Chef, Role.Cook];
    public static readonly Role[] Stock = [Role.Manager, Role.Chef, Role.Storekeeper];
    public static readonly Role[] Purchasing = [Role.Manager, Role.Chef, Role.Storekeeper];
    public static readonly Role[] Analytics = [Role.Manager, Role.Chef];
    public static readonly Role[] Catalog = [Role.Manager, Role.Chef];
    public static readonly Role[] Leads = [Role.Manager];

    /// <summary>Разделы кабинета, которые видит роль; по этому списку сайт строит меню.</summary>
    public static IEnumerable<string> Sections(Role role)
    {
        yield return "today";
        if (Orders.Contains(role)) yield return "orders";
        if (Stock.Contains(role)) yield return "stock";
        if (Orders.Contains(role)) yield return "menu";
        if (Purchasing.Contains(role)) yield return "purchasing";
        if (Analytics.Contains(role)) yield return "analytics";
        if (Catalog.Contains(role)) yield return "catalog";
    }

    public static TBuilder RequireRoles<TBuilder>(this TBuilder builder, Role[] roles) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireRole(roles.Select(r => r.ToString())));
}
