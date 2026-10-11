using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShiftDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastReadAlertId",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Прочитанность была общей на ресторан: каждый сотрудник наследует её, прежде чем колонка исчезнет.
            migrationBuilder.Sql("""
                UPDATE "Users" u SET "LastReadAlertId" = COALESCE(
                    (SELECT MAX(a."Id") FROM "Alerts" a WHERE a."RestaurantId" = u."RestaurantId" AND a."ReadAt" IS NOT NULL), 0)
                """);

            migrationBuilder.DropIndex(
                name: "IX_Alerts_RestaurantId_ReadAt",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "Alerts");

            migrationBuilder.AddColumn<int>(
                name: "LastInventoryNumber",
                table: "Restaurants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadyAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            // Заказы, принятые до появления экрана кухни, давно отданы: в очередь поваров они не попадают.
            migrationBuilder.Sql("""UPDATE "Orders" SET "ReadyAt" = "CreatedAt" WHERE "Status" = 'Accepted'""");

            migrationBuilder.CreateTable(
                name: "Inventories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RestaurantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Shortage = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    Surplus = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Inventories_Restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalTable: "Restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InventoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Accounted = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    Actual = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    Difference = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(14,3)", precision: 14, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryItem_Inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryItem_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RestaurantId",
                table: "Alerts",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_Inventories_RestaurantId_Number",
                table: "Inventories",
                columns: new[] { "RestaurantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItem_InventoryId",
                table: "InventoryItem",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItem_ProductId",
                table: "InventoryItem",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryItem");

            migrationBuilder.DropTable(
                name: "Inventories");

            migrationBuilder.DropIndex(
                name: "IX_Alerts_RestaurantId",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LastReadAlertId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastInventoryNumber",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "ReadyAt",
                table: "Orders");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReadAt",
                table: "Alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RestaurantId_ReadAt",
                table: "Alerts",
                columns: new[] { "RestaurantId", "ReadAt" });
        }
    }
}
