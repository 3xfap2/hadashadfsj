using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ostatok.Api.Data;
using Ostatok.Api.Domain;

namespace Ostatok.Api.Features;

public sealed record LeadRequest(string? Name, string? Organization, string? Contact, string? Points, string? Comment);
public sealed record LeadResponse(long Id, DateTimeOffset CreatedAt, string Name, string Organization, string Contact, string Points, string? Comment);

/// <summary>Заявки на демо с лендинга.</summary>
public static partial class LeadEndpoints
{
    public const string RateLimit = "leads";

    [GeneratedRegex(@"^\+?[\d\s()-]{10,}$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();

    public static void MapLeads(this IEndpointRouteBuilder app)
    {
        // Форму отправляет посетитель без входа, поэтому адрес открыт и ограничен по числу запросов.
        app.MapPost("/api/leads", async (LeadRequest req, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var (name, organization, contact) = (req.Name?.Trim() ?? "", req.Organization?.Trim() ?? "", req.Contact?.Trim() ?? "");
            var errors = new Errors()
                .Check(name is { Length: > 0 and <= 120 }, "name", "Как к вам обращаться?")
                .Check(organization is { Length: > 0 and <= 120 }, "organization", "Укажите название ресторана или сети.")
                .Check(contact.Length <= 120 && (Phone().IsMatch(contact) || Email().IsMatch(contact)), "contact", "Укажите телефон или почту.")
                .Check((req.Points?.Length ?? 0) <= 20, "points", "Число точек указано неверно.")
                .Check((req.Comment?.Length ?? 0) <= 1000, "comment", "Комментарий — до 1000 символов.");
            if (errors.Any) return errors.ToResult();

            var lead = new Lead
            {
                CreatedAt = time.GetUtcNow(),
                Name = name,
                Organization = organization,
                Contact = contact,
                Points = req.Points?.Trim() ?? "",
                Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim(),
            };
            db.Leads.Add(lead);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/leads/{lead.Id}", ToResponse(lead));
        })
        .WithTags("Заявки")
        .AllowAnonymous()
        .RequireRateLimiting(RateLimit);

        app.MapGet("/api/leads", async (AppDbContext db, CancellationToken ct) =>
            (await db.Leads.OrderByDescending(l => l.Id).Take(200).ToListAsync(ct)).Select(ToResponse))
        .WithTags("Заявки")
        .RequireAuthorization();
    }

    private static LeadResponse ToResponse(Lead l) => new(l.Id, l.CreatedAt, l.Name, l.Organization, l.Contact, l.Points, l.Comment);
}
