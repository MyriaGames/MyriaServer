using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class DropSkillCombinationTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterCombinedSkillInputs");

            migrationBuilder.DropTable(
                name: "CharacterCompositeSkillComponents");

            migrationBuilder.DropTable(
                name: "CharacterCombinedSkills");

            migrationBuilder.DropTable(
                name: "CharacterCompositeSkills");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterCombinedSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsStashed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StashedForClass = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCombinedSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCombinedSkills_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCompositeSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsStashed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StashedForClass = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCompositeSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCompositeSkills_Characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCombinedSkillInputs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CombinedSkillId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCombinedSkillInputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCombinedSkillInputs_CharacterCombinedSkills_CombinedSkillId",
                        column: x => x.CombinedSkillId,
                        principalTable: "CharacterCombinedSkills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharacterCompositeSkillComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompositeSkillId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCompositeSkillComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCompositeSkillComponents_CharacterCompositeSkills_CompositeSkillId",
                        column: x => x.CompositeSkillId,
                        principalTable: "CharacterCompositeSkills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCombinedSkillInputs_CombinedSkillId",
                table: "CharacterCombinedSkillInputs",
                column: "CombinedSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCombinedSkills_CharacterId",
                table: "CharacterCombinedSkills",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCompositeSkillComponents_CompositeSkillId",
                table: "CharacterCompositeSkillComponents",
                column: "CompositeSkillId");

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCompositeSkills_CharacterId",
                table: "CharacterCompositeSkills",
                column: "CharacterId");
        }
    }
}
