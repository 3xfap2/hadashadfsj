using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supply> Supplies => Set<Supply>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Dish> Dishes => Set<Dish>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Movement> Movements => Set<Movement>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Lead> Leads => Set<Lead>();

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
            e.HasIndex(p => p.Name).IsUnique();
        });

        model.Entity<Supply>(e =>
        {
            e.Property(s => s.Number).HasMaxLength(40);
            e.HasIndex(s => s.Number).IsUnique();
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
            e.HasIndex(d => d.Name).IsUnique();
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

        model.Entity<Movement>(e =>
        {
            e.Property(m => m.Note).HasMaxLength(200);
            e.Property(m => m.Actor).HasMaxLength(60);
            e.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => m.At);
        });

        model.Entity<Lead>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(120);
            e.Property(l => l.Organization).HasMaxLength(120);
            e.Property(l => l.Contact).HasMaxLength(120);
            e.Property(l => l.Points).HasMaxLength(20);
            e.Property(l => l.Comment).HasMaxLength(1000);
        });

        model.Entity<User>(e =>
        {
            e.Property(u => u.Login).HasMaxLength(60);
            e.Property(u => u.Title).HasMaxLength(60);
            e.HasIndex(u => u.Login).IsUnique();
        });
    }
}
