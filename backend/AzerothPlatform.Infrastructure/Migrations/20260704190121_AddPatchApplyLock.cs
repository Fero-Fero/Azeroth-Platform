using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPatchApplyLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplyRunId",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApplyStartedAt",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApplyingPatchKey",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplyRunId",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ApplyStartedAt",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "ApplyingPatchKey",
                table: "ManagedStacks");
        }
    }
}
