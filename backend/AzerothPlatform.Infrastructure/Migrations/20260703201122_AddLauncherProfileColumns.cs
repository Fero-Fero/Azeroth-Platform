using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLauncherProfileColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LauncherDescription",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LauncherDisplayName",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LauncherSortOrder",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "LauncherVisible",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RealmlistHostOverride",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LauncherDescription",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "LauncherDisplayName",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "LauncherSortOrder",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "LauncherVisible",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "RealmlistHostOverride",
                table: "ManagedStacks");
        }
    }
}
