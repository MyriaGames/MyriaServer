using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.ClassicRealm.Migrations
{
    /// <inheritdoc />
    public partial class AddPvpStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PvpCurrency",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "PvpLosses",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing characters start at the same rating as new ones (PvpRatingService.StartingRating
            // at the time of writing; a literal so this migration never changes). EF doesn't pick up
            // the model's property initializer, so the generated default was 0.
            migrationBuilder.AddColumn<int>(
                name: "PvpRating",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.AddColumn<int>(
                name: "PvpWins",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PvpCurrency",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpLosses",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpRating",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "PvpWins",
                table: "Characters");
        }
    }
}
