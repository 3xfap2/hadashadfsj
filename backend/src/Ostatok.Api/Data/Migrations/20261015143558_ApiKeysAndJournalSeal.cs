using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeysAndJournalSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Delivery",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Orders",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "Movements",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SealNo",
                table: "Movements",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SealedAt",
                table: "Movements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RestaurantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CanSupplies = table.Column<bool>(type: "boolean", nullable: false),
                    CanOrders = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalTable: "Restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_RestaurantId_SealNo",
                table: "Movements",
                columns: new[] { "RestaurantId", "SealNo" },
                unique: true,
                filter: "\"SealNo\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_Unsealed",
                table: "Movements",
                columns: new[] { "RestaurantId", "Id" },
                filter: "\"SealNo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Hash",
                table: "ApiKeys",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_RestaurantId",
                table: "ApiKeys",
                column: "RestaurantId");

            // Ключ, выданный раньше (единственный на ресторан), продолжает работать: он переносится как «Ключ поставщика»
            // с правом присылать поставки. Столбец Restaurants.IntegrationKeyHash остаётся, но больше не читается.
            migrationBuilder.Sql("""
                INSERT INTO "ApiKeys" ("Id", "RestaurantId", "Name", "Hash", "CanSupplies", "CanOrders", "CreatedAt", "CreatedBy")
                SELECT gen_random_uuid(), "Id", 'Ключ поставщика', "IntegrationKeyHash", TRUE, FALSE, "CreatedAt", ''
                FROM "Restaurants" WHERE "IntegrationKeyHash" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_Movements_RestaurantId_SealNo",
                table: "Movements");

            migrationBuilder.DropIndex(
                name: "IX_Movements_Unsealed",
                table: "Movements");

            migrationBuilder.DropColumn(
                name: "Delivery",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "Movements");

            migrationBuilder.DropColumn(
                name: "SealNo",
                table: "Movements");

            migrationBuilder.DropColumn(
                name: "SealedAt",
                table: "Movements");
        }
    }
}
