using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentTargetColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing stacks are all local; store the enum's string form so the value converter reads
            // them back correctly (an empty string would fail to parse to DeploymentTarget).
            migrationBuilder.AddColumn<string>(
                name: "DeploymentTarget",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.AddColumn<string>(
                name: "ExternalHost",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ExternalSshPort",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSshPrivateKey",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExternalSshUser",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeploymentTarget",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ExternalHost",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ExternalSshPort",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ExternalSshPrivateKey",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ExternalSshUser",
                table: "ManagedStacks");
        }
    }
}
