using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <summary>
    /// Владелец ресторана, открытого командой продукта, записывается явно. Раньше им считался любой управляющий
    /// ресторана, добавивший себе другие рестораны; теперь — тот из них, кто добавил свой ресторан первым.
    /// Схема не меняется, только данные.
    /// </summary>
    public partial class ExplicitRestaurantOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Restaurants" AS r SET "OwnerLogin" = (
                    SELECT u."Login" FROM "Users" AS u JOIN "Restaurants" AS o ON o."OwnerLogin" = u."Login"
                    WHERE u."RestaurantId" = r."Id" AND u."Role" = 'Manager'
                    ORDER BY o."CreatedAt", o."Id" LIMIT 1)
                WHERE r."OwnerLogin" IS NULL AND EXISTS (
                    SELECT 1 FROM "Users" AS u JOIN "Restaurants" AS o ON o."OwnerLogin" = u."Login"
                    WHERE u."RestaurantId" = r."Id" AND u."Role" = 'Manager');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Какие записи были пустыми, не сохранено; явный владелец не мешает и старому коду.
        }
    }
}
