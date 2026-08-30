using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AzerothCoreDbContext))]
    [Migration("20260820223000_AddExpressProvisionPhase")]
    public partial class AddExpressProvisionPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpressProvisionPhase",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<bool>(
                name: "ExpressGameAccountCreated",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExpressReadyNoticePending",
                table: "ManagedStacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ExpressProvisionPhase", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "ExpressGameAccountCreated", table: "ManagedStacks");
            migrationBuilder.DropColumn(name: "ExpressReadyNoticePending", table: "ManagedStacks");
        }
    }
}
