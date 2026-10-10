using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Restaurants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Supplies_Number",
                table: "Supplies");

            migrationBuilder.DropIndex(
                name: "IX_Products_Name",
                table: "Products");

            migrationBuilder.DropPrimaryKey(
                name: "PK_HotSettings",
                table: "HotSettings");

            migrationBuilder.DropIndex(
                name: "IX_Dishes_Name",
                table: "Dishes");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "HotSettings");

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Supplies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "StopLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Products",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Orders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Movements",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "HotSettings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Dishes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Batches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Alerts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_HotSettings",
                table: "HotSettings",
                column: "RestaurantId");

            migrationBuilder.CreateTable(
                name: "Restaurants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Restaurants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Supplies_RestaurantId_Number",
                table: "Supplies",
                columns: new[] { "RestaurantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_RestaurantId_Name",
                table: "Products",
                columns: new[] { "RestaurantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dishes_RestaurantId_Name",
                table: "Dishes",
                columns: new[] { "RestaurantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Restaurants");

            migrationBuilder.DropIndex(
                name: "IX_Supplies_RestaurantId_Number",
                table: "Supplies");

            migrationBuilder.DropIndex(
                name: "IX_Products_RestaurantId_Name",
                table: "Products");

            migrationBuilder.DropPrimaryKey(
                name: "PK_HotSettings",
                table: "HotSettings");

            migrationBuilder.DropIndex(
                name: "IX_Dishes_RestaurantId_Name",
                table: "Dishes");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Supplies");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "StopLogs");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Movements");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "HotSettings");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Dishes");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Alerts");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "HotSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_HotSettings",
                table: "HotSettings",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Supplies_Number",
                table: "Supplies",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dishes_Name",
                table: "Dishes",
                column: "Name",
                unique: true);
        }
    }
}
