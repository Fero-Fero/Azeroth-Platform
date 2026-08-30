using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudConnectionAuthMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountHint",
                table: "CloudProviderConnections",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AuthMethod",
                table: "CloudProviderConnections",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<bool>(
                name: "NeedsReauth",
                table: "CloudProviderConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TokenExpiresAtUtc",
                table: "CloudProviderConnections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountHint",
                table: "CloudProviderConnections");

            migrationBuilder.DropColumn(
                name: "AuthMethod",
                table: "CloudProviderConnections");

            migrationBuilder.DropColumn(
                name: "NeedsReauth",
                table: "CloudProviderConnections");

            migrationBuilder.DropColumn(
                name: "TokenExpiresAtUtc",
                table: "CloudProviderConnections");
        }
    }
}
