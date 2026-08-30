using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AzerothCoreDbContext))]
    [Migration("20260819020000_AddIncludeArmoryAndExpressColumns")]
    public partial class AddIncludeArmoryAndExpressColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeArmory",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "RandomBotCount",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExpressProvisionStatus",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "ExpressProvisionMessage",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddonIdsJson",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IncludeArmory", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "RandomBotCount", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "ExpressProvisionStatus", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "ExpressProvisionMessage", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "AddonIdsJson", table: "ManagedStacks");
        }
    }
}
