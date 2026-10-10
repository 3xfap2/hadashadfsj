using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class HotSettingsAndPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HotHidden",
                table: "Dishes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HotPinned",
                table: "Dishes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "HotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    HorizonHours = table.Column<double>(type: "double precision", nullable: false),
                    UrgencyWeight = table.Column<int>(type: "integer", nullable: false),
                    ShareAcrossDishes = table.Column<bool>(type: "boolean", nullable: false),
                    MaxDiscountPercent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HotSettings");

            migrationBuilder.DropColumn(
                name: "HotHidden",
                table: "Dishes");

            migrationBuilder.DropColumn(
                name: "HotPinned",
                table: "Dishes");
        }
    }
}
