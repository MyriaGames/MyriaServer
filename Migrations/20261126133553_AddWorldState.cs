using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.ClassicRealm.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorldNpcStock",
                columns: table => new
                {
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    NpcId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldNpcStock", x => new { x.RoomId, x.NpcId, x.ItemId });
                });

            migrationBuilder.CreateTable(
                name: "WorldRoomPopulations",
                columns: table => new
                {
                    RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonsterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldRoomPopulations", x => new { x.RoomId, x.MonsterId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorldNpcStock");

            migrationBuilder.DropTable(
                name: "WorldRoomPopulations");
        }
    }
}
