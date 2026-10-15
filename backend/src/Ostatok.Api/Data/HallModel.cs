using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Data;

/// <summary>Таблицы зала: зоны, столы, брони, визиты, блокировки, настройки и журнал.</summary>
public static class HallModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<HallZone>(e =>
        {
            e.Property(z => z.Name).HasMaxLength(60);
            e.HasIndex(z => new { z.RestaurantId, z.Sort });
        });

        model.Entity<HallTable>(e =>
        {
            e.HasOne<HallZone>().WithMany().HasForeignKey(t => t.ZoneId).OnDelete(DeleteBehavior.Restrict);
            // Номер стола — место заказа («Стол 5»): у действующих столов ресторана он не повторяется.
            e.HasIndex(t => new { t.RestaurantId, t.Number }).IsUnique().HasFilter("NOT \"Archived\"");
            e.Property(t => t.Version).IsConcurrencyToken();
            e.ToTable(t => t.HasCheckConstraint("CK_HallTables_Seats", "\"Seats\" > 0"));
        });

        model.Entity<Reservation>(e =>
        {
            e.HasOne<HallTable>().WithMany().HasForeignKey(r => r.TableId).OnDelete(DeleteBehavior.Restrict);
            // Имя и телефон гостя — личные данные, как у заявок с лендинга.
            e.Property(r => r.Guest).HasConversion(PersonalData.Converter);
            e.Property(r => r.Phone).HasConversion(PersonalData.NullableConverter);
            e.Property(r => r.Note).HasMaxLength(300);
            e.Property(r => r.Source).HasMaxLength(30);
            e.Property(r => r.CreatedBy).HasMaxLength(60);
            e.Property(r => r.Version).IsConcurrencyToken();
            e.HasIndex(r => new { r.TableId, r.StartsAt });
            e.HasIndex(r => new { r.RestaurantId, r.StartsAt });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Reservations_Interval", "\"EndsAt\" > \"StartsAt\"");
                t.HasCheckConstraint("CK_Reservations_Guests", "\"Guests\" > 0");
            });
        });

        model.Entity<Visit>(e =>
        {
            e.HasOne<HallTable>().WithMany().HasForeignKey(v => v.TableId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Reservation>().WithMany().HasForeignKey(v => v.ReservationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(v => v.SeatedBy).HasMaxLength(60);
            e.Property(v => v.EndedBy).HasMaxLength(60);
            e.Property(v => v.ReadyBy).HasMaxLength(60);
            // Последний рубеж против двух посадок: за столом не бывает двух незавершённых визитов,
            // по одной брони — двух визитов.
            e.HasIndex(v => v.TableId).IsUnique().HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("IX_Visits_Active");
            e.HasIndex(v => v.ReservationId).IsUnique().HasFilter("\"ReservationId\" IS NOT NULL");
            e.HasIndex(v => new { v.TableId, v.SeatedAt });
        });

        model.Entity<TableBlock>(e =>
        {
            e.HasOne<HallTable>().WithMany().HasForeignKey(b => b.TableId).OnDelete(DeleteBehavior.Restrict);
            e.Property(b => b.Reason).HasMaxLength(200);
            e.Property(b => b.CreatedBy).HasMaxLength(60);
            e.Property(b => b.LiftedBy).HasMaxLength(60);
            e.HasIndex(b => new { b.TableId, b.From });
        });

        model.Entity<HallSettings>(e => e.HasKey(s => s.RestaurantId));

        model.Entity<HallEvent>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(60);
            e.Property(x => x.Entity).HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Text).HasMaxLength(500);
            e.HasIndex(x => new { x.RestaurantId, x.At });
        });
    }
}
