using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OpenedShelfLife : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OpenedShelfLifeHours",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OpenedAt",
                table: "Batches",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenedShelfLifeHours",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "OpenedAt",
                table: "Batches");
        }
    }
}
