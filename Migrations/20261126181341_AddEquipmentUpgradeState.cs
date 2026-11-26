using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Myria.Server.Realm.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentUpgradeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "AccessoryCraftQuality",
                table: "Characters",
                type: "REAL",
                nullable: false,
                defaultValue: 1f); // existing rows: normal quality, not 0

            migrationBuilder.AddColumn<int>(
                name: "AccessoryUpgradeLevel",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "ArmorCraftQuality",
                table: "Characters",
                type: "REAL",
                nullable: false,
                defaultValue: 1f); // existing rows: normal quality, not 0

            migrationBuilder.AddColumn<int>(
                name: "ArmorUpgradeLevel",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "WeaponCraftQuality",
                table: "Characters",
                type: "REAL",
                nullable: false,
                defaultValue: 1f); // existing rows: normal quality, not 0

            migrationBuilder.AddColumn<int>(
                name: "WeaponUpgradeLevel",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "CraftQuality",
                table: "CharacterInventoryItems",
                type: "REAL",
                nullable: false,
                defaultValue: 1f); // existing rows: normal quality, not 0

            migrationBuilder.AddColumn<int>(
                name: "UpgradeLevel",
                table: "CharacterInventoryItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessoryCraftQuality",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "AccessoryUpgradeLevel",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "ArmorCraftQuality",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "ArmorUpgradeLevel",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "WeaponCraftQuality",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "WeaponUpgradeLevel",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "CraftQuality",
                table: "CharacterInventoryItems");

            migrationBuilder.DropColumn(
                name: "UpgradeLevel",
                table: "CharacterInventoryItems");
        }
    }
}
