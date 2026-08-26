using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerShops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerShops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerShops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerShops_Characters_OwnerCharacterId",
                        column: x => x.OwnerCharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlayerShopItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayerShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerShopItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerShopItems_PlayerShops_PlayerShopId",
                        column: x => x.PlayerShopId,
                        principalTable: "PlayerShops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerShopItems_PlayerShopId_ItemId",
                table: "PlayerShopItems",
                columns: new[] { "PlayerShopId", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerShops_OwnerCharacterId",
                table: "PlayerShops",
                column: "OwnerCharacterId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerShopItems");

            migrationBuilder.DropTable(
                name: "PlayerShops");
        }
    }
}
