using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterSkillProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UsageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    UnspentPoints = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterSkillProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterSkillProgress_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterSkillProgressUpgrades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SkillProgressId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterSkillProgressUpgrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterSkillProgressUpgrades_CharacterSkillProgress_SkillProgressId",
                        column: x => x.SkillProgressId,
                        principalTable: "CharacterSkillProgress",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterSkillProgress_CharacterId",
                table: "CharacterSkillProgress",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterSkillProgressUpgrades_SkillProgressId",
                table: "CharacterSkillProgressUpgrades",
                column: "SkillProgressId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterSkillProgressUpgrades");

            migrationBuilder.DropTable(
                name: "CharacterSkillProgress");
        }
    }
}
