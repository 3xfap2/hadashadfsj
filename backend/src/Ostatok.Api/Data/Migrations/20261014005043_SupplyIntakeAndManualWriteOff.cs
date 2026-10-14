using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SupplyIntakeAndManualWriteOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoWriteOff",
                table: "Restaurants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoWriteOffAfterHours",
                table: "Restaurants",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationKeyHash",
                table: "Restaurants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierInn",
                table: "IncomingSupplies",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Accepted",
                table: "IncomingItem",
                type: "numeric(14,3)",
                precision: 14,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Restaurants_IntegrationKeyHash",
                table: "Restaurants",
                column: "IntegrationKeyHash",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingItems_Accepted",
                table: "IncomingItem",
                sql: "\"Accepted\" IS NULL OR (\"Accepted\" >= 0 AND \"Accepted\" <= \"Quantity\")");

            // Поставки, принятые до частичной приёмки, принимались целиком: принято столько, сколько заказано.
            migrationBuilder.Sql("""
                UPDATE "IncomingItem" i SET "Accepted" = i."Quantity"
                FROM "IncomingSupplies" s
                WHERE s."Id" = i."IncomingSupplyId" AND s."Status" = 'Accepted'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Restaurants_IntegrationKeyHash",
                table: "Restaurants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingItems_Accepted",
                table: "IncomingItem");

            migrationBuilder.DropColumn(
                name: "AutoWriteOff",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "AutoWriteOffAfterHours",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "IntegrationKeyHash",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "SupplierInn",
                table: "IncomingSupplies");

            migrationBuilder.DropColumn(
                name: "Accepted",
                table: "IncomingItem");
        }
    }
}
