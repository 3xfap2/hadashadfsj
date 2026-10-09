using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ostatok.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Leads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Leads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Organization = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Contact = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Points = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leads", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Leads");
        }
    }
}
