using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <summary>
    /// Ресторан, открытый командой продукта, принадлежит своему управляющему: раньше владелец записывался,
    /// только когда управляющий добавлял себе второй ресторан, и до этого кабинет владельца ему не открывался.
    /// Владелец записывается, если управляющий в ресторане один; демо-ресторан не трогается. Схема не меняется.
    /// </summary>
    public partial class OwnerOfCrmRestaurants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Restaurants" AS r SET "OwnerLogin" = (
                    SELECT u."Login" FROM "Users" AS u WHERE u."RestaurantId" = r."Id" AND u."Role" = 'Manager')
                WHERE r."OwnerLogin" IS NULL AND r."Id" <> '0d3a0c1e-0000-4000-8000-000000000001'
                  AND (SELECT count(*) FROM "Users" AS u WHERE u."RestaurantId" = r."Id" AND u."Role" = 'Manager') = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Какие записи были пустыми, не сохранено; явный владелец не мешает и старому коду.
        }
    }
}
