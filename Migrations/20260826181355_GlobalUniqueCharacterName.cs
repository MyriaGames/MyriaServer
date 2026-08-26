using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class GlobalUniqueCharacterName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Characters_UserId_Name",
                table: "Characters");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Characters",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 50);

            migrationBuilder.CreateIndex(
                name: "IX_Characters_Name",
                table: "Characters",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Characters_Name",
                table: "Characters");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Characters",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 50,
                oldCollation: "NOCASE");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_UserId_Name",
                table: "Characters",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }
    }
}
