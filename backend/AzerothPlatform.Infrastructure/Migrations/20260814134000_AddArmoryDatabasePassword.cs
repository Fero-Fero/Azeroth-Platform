using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArmoryDatabasePassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArmoryDatabasePasswordProtected",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArmoryDatabasePasswordProtected",
                table: "ManagedStacks");
        }
    }
}
