using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AzerothCoreDbContext))]
    [Migration("20260820160000_AddModuleCheckColumns")]
    public partial class AddModuleCheckColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModuleBranchesJson",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "ModuleCheckFingerprint",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModuleCheckJson",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ModuleBranchesJson", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "ModuleCheckFingerprint", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "ModuleCheckJson", table: "ManagedStacks");
        }
    }
}
