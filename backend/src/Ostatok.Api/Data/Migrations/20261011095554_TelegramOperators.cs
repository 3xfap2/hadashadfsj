using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelegramOperators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramOperators",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramOperators", x => x.ChatId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramOperators");
        }
    }
}
