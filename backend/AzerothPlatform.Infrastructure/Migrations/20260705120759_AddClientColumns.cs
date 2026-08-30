using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClientEnabled",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ClientPort",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientEnabled",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ClientPort",
                table: "ManagedStacks");
        }
    }
}
