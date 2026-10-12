using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Crm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisabledAt",
                table: "Restaurants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Assignee",
                table: "Leads",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RestaurantId",
                table: "Leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Leads",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Заявки, пришедшие до CRM, никто не разбирал: все они новые.
                defaultValue: "New");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Leads",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Прежние заявки не менялись с момента создания.
            migrationBuilder.Sql("""
                UPDATE "Leads" SET "UpdatedAt" = "CreatedAt";
                """);

            migrationBuilder.AddColumn<long>(
                name: "LeadId",
                table: "Chats",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CrmNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Author = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: true),
                    RestaurantId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmNotes", x => x.Id);
                    table.CheckConstraint("CK_CrmNotes_Target", "(\"LeadId\" IS NULL) <> (\"RestaurantId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CrmNotes_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CrmNotes_Restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalTable: "Restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CrmTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeadId = table.Column<long>(type: "bigint", nullable: true),
                    RestaurantId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmTasks_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CrmTasks_Restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalTable: "Restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_RestaurantId",
                table: "Leads",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_LeadId",
                table: "Chats",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmNotes_LeadId",
                table: "CrmNotes",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmNotes_RestaurantId",
                table: "CrmNotes",
                column: "RestaurantId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_DueAt",
                table: "CrmTasks",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_LeadId",
                table: "CrmTasks",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_RestaurantId",
                table: "CrmTasks",
                column: "RestaurantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Chats_Leads_LeadId",
                table: "Chats",
                column: "LeadId",
                principalTable: "Leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Restaurants_RestaurantId",
                table: "Leads",
                column: "RestaurantId",
                principalTable: "Restaurants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chats_Leads_LeadId",
                table: "Chats");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Restaurants_RestaurantId",
                table: "Leads");

            migrationBuilder.DropTable(
                name: "CrmNotes");

            migrationBuilder.DropTable(
                name: "CrmTasks");

            migrationBuilder.DropIndex(
                name: "IX_Leads_RestaurantId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Chats_LeadId",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "DisabledAt",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "Assignee",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "RestaurantId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "LeadId",
                table: "Chats");
        }
    }
}
