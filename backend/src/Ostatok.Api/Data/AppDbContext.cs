using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

/// <summary>Ресторан, от имени которого идёт работа: берётся из токена, в фоновых задачах задаётся явно.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options, Tenant tenant) : DbContext(options)
{
    /// <summary>Текущий ресторан. Все запросы к его данным отфильтрованы по этому значению.</summary>
    public Guid TenantId => tenant.Id;

    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supply> Supplies => Set<Supply>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Dish> Dishes => Set<Dish>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Movement> Movements => Set<Movement>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<HotSettings> HotSettings => Set<HotSettings>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<DishPhoto> DishPhotos => Set<DishPhoto>();
    public DbSet<StopLog> StopLogs => Set<StopLog>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<decimal>().HavePrecision(14, 3);
        configuration.Properties<Enum>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Product>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(120);
            e.HasIndex(p => new { p.RestaurantId, p.Name }).IsUnique();
        });

        model.Entity<Supply>(e =>
        {
            e.Property(s => s.Number).HasMaxLength(40);
            e.Property(s => s.Supplier).HasMaxLength(120);
            e.HasIndex(s => new { s.RestaurantId, s.Number }).IsUnique();
        });

        model.Entity<Batch>(e =>
        {
            e.HasOne(b => b.Supply).WithMany(s => s.Batches).HasForeignKey(b => b.SupplyId);
            e.HasOne(b => b.Product).WithMany().HasForeignKey(b => b.ProductId).OnDelete(DeleteBehavior.Restrict);
            // Выборка годных партий продукта в порядке срока — основной запрос при заказе.
            e.HasIndex(b => new { b.ProductId, b.ExpiresAt });
            e.ToTable(t => t.HasCheckConstraint("CK_Batches_Remaining", "\"Remaining\" >= 0 AND \"Remaining\" <= \"Quantity\""));
        });

        model.Entity<Dish>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(120);
            e.Property(d => d.PhotoUrl).HasMaxLength(300);
            e.Property(d => d.Category).HasMaxLength(60);
            e.HasIndex(d => new { d.RestaurantId, d.Name }).IsUnique();
            e.HasMany(d => d.Recipe).WithOne().HasForeignKey(r => r.DishId);
        });

        model.Entity<RecipeItem>(e =>
        {
            e.HasKey(r => new { r.DishId, r.ProductId });
            e.HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Order>(e =>
        {
            e.Property(o => o.Number).UseIdentityAlwaysColumn();
            e.Property(o => o.Place).HasMaxLength(40);
            e.HasIndex(o => o.CreatedAt);
            e.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId);
            e.HasMany(o => o.Shortages).WithOne().HasForeignKey(s => s.OrderId);
        });

        model.Entity<OrderItem>(e =>
        {
            e.HasKey(i => new { i.OrderId, i.DishId });
            e.HasOne(i => i.Dish).WithMany().HasForeignKey(i => i.DishId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OrderShortage>(e =>
        {
            e.HasKey(s => new { s.OrderId, s.ProductId });
            e.HasOne(s => s.Product).WithMany().HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<HotSettings>(e => e.HasKey(s => s.RestaurantId));
        model.Entity<Restaurant>(e => e.Property(r => r.Name).HasMaxLength(120));

        model.Entity<PrepItem>(e =>
        {
            e.HasKey(i => new { i.PrepId, i.ProductId });
            e.HasOne<Product>().WithMany(p => p.PrepRecipe).HasForeignKey(i => i.PrepId);
            e.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Movement>(e =>
        {
            e.Property(m => m.Note).HasMaxLength(200);
            e.Property(m => m.Actor).HasMaxLength(60);
            e.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => m.At);
        });

        model.Entity<DishPhoto>(e =>
        {
            e.HasKey(p => p.DishId);
            e.HasOne<Dish>().WithOne().HasForeignKey<DishPhoto>(p => p.DishId);
            e.Property(p => p.ContentType).HasMaxLength(40);
        });

        model.Entity<StopLog>(e =>
        {
            e.HasOne(l => l.Dish).WithMany().HasForeignKey(l => l.DishId);
            e.HasIndex(l => l.StartedAt);
        });

        model.Entity<Alert>(e =>
        {
            e.Property(a => a.Title).HasMaxLength(200);
            e.Property(a => a.Detail).HasMaxLength(300);
            // Один сигнал каждого вида на партию: повторный проход не создаёт дубль.
            e.HasIndex(a => new { a.BatchId, a.Kind }).IsUnique().HasFilter("\"BatchId\" IS NOT NULL");
        });

        model.Entity<Lead>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(120);
            e.Property(l => l.Organization).HasMaxLength(120);
            e.Property(l => l.Contact).HasMaxLength(120);
            e.Property(l => l.Points).HasMaxLength(20);
            e.Property(l => l.Comment).HasMaxLength(1000);
        });

        model.Entity<Chat>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(120);
            e.Property(c => c.Contact).HasMaxLength(120);
            e.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ChatId);
            e.HasIndex(c => c.LastMessageAt);
        });

        model.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.Author).HasMaxLength(100);
            e.Property(m => m.Text).HasMaxLength(1000);
        });

        model.Entity<User>(e =>
        {
            e.Property(u => u.Login).HasMaxLength(60);
            e.Property(u => u.Title).HasMaxLength(60);
            e.HasIndex(u => u.Login).IsUnique();
        });

        // Каждая запись ресторана видна только ему: фильтр добавляется ко всем запросам автоматически.
        foreach (var type in model.Model.GetEntityTypes().Where(t => typeof(IOwned).IsAssignableFrom(t.ClrType)).ToList())
            GetType().GetMethod(nameof(FilterByRestaurant), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(type.ClrType).Invoke(this, [model]);
    }

    private void FilterByRestaurant<T>(ModelBuilder model) where T : class, IOwned =>
        model.Entity<T>().HasQueryFilter(e => e.RestaurantId == TenantId);

    /// <summary>Новая запись получает ресторан того, кто её создаёт, если он не задан явно.</summary>
    private void Own()
    {
        foreach (var entry in ChangeTracker.Entries<IOwned>())
            if (entry.State == EntityState.Added && entry.Entity.RestaurantId == Guid.Empty)
                entry.Entity.RestaurantId = TenantId;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Own();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        Own();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
}
