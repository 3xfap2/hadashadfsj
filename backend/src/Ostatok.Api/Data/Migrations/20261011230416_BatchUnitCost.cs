using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BatchUnitCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "Batches",
                type: "numeric(14,3)",
                precision: 14,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            // У партий, принятых до этой версии, цены не было: им достаётся цена продукта на момент обновления.
            migrationBuilder.Sql("""
                UPDATE "Batches" b SET "UnitCost" = p."Cost" FROM "Products" p WHERE p."Id" = b."ProductId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "Batches");
        }
    }
}
