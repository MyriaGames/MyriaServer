using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.ClassicRealm.Migrations
{
    /// <inheritdoc />
    public partial class AddDynamicQuests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdventurersGuildMember",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "QuestGrants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    NpcId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    MonsterId = table.Column<int>(type: "INTEGER", nullable: true),
                    Target = table.Column<int>(type: "INTEGER", nullable: false),
                    RewardXp = table.Column<int>(type: "INTEGER", nullable: false),
                    RewardGold = table.Column<int>(type: "INTEGER", nullable: false),
                    HolderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestGrants", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuestGrants");

            migrationBuilder.DropColumn(
                name: "AdventurersGuildMember",
                table: "Characters");
        }
    }
}
