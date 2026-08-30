using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStackRevisionsAndPostBuildAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostBuildAction",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateTable(
                name: "StackRevisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StackId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CoreCommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ModuleVersionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedPatchLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedPatchesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StackRevisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StackRevisions_StackId",
                table: "StackRevisions",
                column: "StackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StackRevisions");

            migrationBuilder.DropColumn(
                name: "PostBuildAction",
                table: "ManagedStacks");
        }
    }
}
