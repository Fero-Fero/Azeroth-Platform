using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArmoryEmailConfirmationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArmoryEmailConfigJson",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ArmoryEmailConfigured",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ArmoryEmailSmtpPasswordProtected",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ArmoryUseEmailConfirmation",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArmoryEmailConfigJson",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ArmoryEmailConfigured",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ArmoryEmailSmtpPasswordProtected",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ArmoryUseEmailConfirmation",
                table: "ManagedStacks");
        }
    }
}
