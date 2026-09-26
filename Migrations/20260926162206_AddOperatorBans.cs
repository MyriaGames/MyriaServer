using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BanReason",
                table: "Characters",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BannedUntil",
                table: "Characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountBans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    BannedUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountBans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountBans_Username",
                table: "AccountBans",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountBans");

            migrationBuilder.DropColumn(
                name: "BanReason",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "BannedUntil",
                table: "Characters");
        }
    }
}
