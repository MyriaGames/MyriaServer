using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.ClassicRealm.Migrations
{
    /// <inheritdoc />
    public partial class AddLastingBuffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastingBuffsJson",
                table: "Characters",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastingBuffsJson",
                table: "Characters");
        }
    }
}
