using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_RestaurantId_Number",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Movements_At",
                table: "Movements");

            migrationBuilder.AddColumn<int>(
                name: "LastCheckNumber",
                table: "Restaurants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastPrepNumber",
                table: "Restaurants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DiscountQuantity",
                table: "OrderItem",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "OrderItem",
                type: "numeric(14,3)",
                precision: 14,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_Users_RestaurantId",
                table: "Users",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_StopLogs_RestaurantId_EndedAt",
                table: "StopLogs",
                columns: new[] { "RestaurantId", "EndedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeItem_Quantity",
                table: "RecipeItem",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PrepItem_Quantity",
                table: "PrepItem",
                sql: "\"Quantity\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RestaurantId_Number",
                table: "Orders",
                columns: new[] { "RestaurantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_BatchId",
                table: "Movements",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OrderId",
                table: "Movements",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_RestaurantId_At",
                table: "Movements",
                columns: new[] { "RestaurantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Batches_RestaurantId",
                table: "Batches",
                column: "RestaurantId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Batches_Quantity",
                table: "Batches",
                sql: "\"Quantity\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RestaurantId_ReadAt",
                table: "Alerts",
                columns: new[] { "RestaurantId", "ReadAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Alerts_Restaurants_RestaurantId",
                table: "Alerts",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Batches_Restaurants_RestaurantId",
                table: "Batches",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Dishes_Restaurants_RestaurantId",
                table: "Dishes",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HotSettings_Restaurants_RestaurantId",
                table: "HotSettings",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Movements_Batches_BatchId",
                table: "Movements",
                column: "BatchId",
                principalTable: "Batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Movements_Orders_OrderId",
                table: "Movements",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Movements_Restaurants_RestaurantId",
                table: "Movements",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Restaurants_RestaurantId",
                table: "Orders",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Restaurants_RestaurantId",
                table: "Products",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StopLogs_Restaurants_RestaurantId",
                table: "StopLogs",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Supplies_Restaurants_RestaurantId",
                table: "Supplies",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Restaurants_RestaurantId",
                table: "Users",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Alerts_Restaurants_RestaurantId",
                table: "Alerts");

            migrationBuilder.DropForeignKey(
                name: "FK_Batches_Restaurants_RestaurantId",
                table: "Batches");

            migrationBuilder.DropForeignKey(
                name: "FK_Dishes_Restaurants_RestaurantId",
                table: "Dishes");

            migrationBuilder.DropForeignKey(
                name: "FK_HotSettings_Restaurants_RestaurantId",
                table: "HotSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_Movements_Batches_BatchId",
                table: "Movements");

            migrationBuilder.DropForeignKey(
                name: "FK_Movements_Orders_OrderId",
                table: "Movements");

            migrationBuilder.DropForeignKey(
                name: "FK_Movements_Restaurants_RestaurantId",
                table: "Movements");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Restaurants_RestaurantId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_Restaurants_RestaurantId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_StopLogs_Restaurants_RestaurantId",
                table: "StopLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_Supplies_Restaurants_RestaurantId",
                table: "Supplies");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Restaurants_RestaurantId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_RestaurantId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_StopLogs_RestaurantId_EndedAt",
                table: "StopLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeItem_Quantity",
                table: "RecipeItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PrepItem_Quantity",
                table: "PrepItem");

            migrationBuilder.DropIndex(
                name: "IX_Orders_RestaurantId_Number",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Movements_BatchId",
                table: "Movements");

            migrationBuilder.DropIndex(
                name: "IX_Movements_OrderId",
                table: "Movements");

            migrationBuilder.DropIndex(
                name: "IX_Movements_RestaurantId_At",
                table: "Movements");

            migrationBuilder.DropIndex(
                name: "IX_Batches_RestaurantId",
                table: "Batches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Batches_Quantity",
                table: "Batches");

            migrationBuilder.DropIndex(
                name: "IX_Alerts_RestaurantId_ReadAt",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LastCheckNumber",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "LastPrepNumber",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "DiscountQuantity",
                table: "OrderItem");

            migrationBuilder.DropColumn(
                name: "Price",
                table: "OrderItem");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RestaurantId_Number",
                table: "Orders",
                columns: new[] { "RestaurantId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_At",
                table: "Movements",
                column: "At");
        }
    }
}
