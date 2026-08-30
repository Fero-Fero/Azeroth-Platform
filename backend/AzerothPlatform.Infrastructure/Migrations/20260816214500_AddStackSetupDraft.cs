using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AzerothCoreDbContext))]
    [Migration("20260816214500_AddStackSetupDraft")]
    public partial class AddStackSetupDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WizardDraftJson",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WizardStepId",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WizardDraftJson",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "WizardStepId",
                table: "ManagedStacks");
        }
    }
}
