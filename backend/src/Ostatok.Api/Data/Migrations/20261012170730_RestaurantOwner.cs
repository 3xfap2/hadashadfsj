using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestaurantOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerLogin",
                table: "Restaurants",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Restaurants_OwnerLogin",
                table: "Restaurants",
                column: "OwnerLogin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Restaurants_OwnerLogin",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "OwnerLogin",
                table: "Restaurants");
        }
    }
}
