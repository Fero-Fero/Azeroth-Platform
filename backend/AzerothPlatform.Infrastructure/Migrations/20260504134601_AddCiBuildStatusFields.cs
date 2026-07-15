using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCiBuildStatusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LatestCoreBuildChecksJson",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestCoreBuildStatus",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestCoreBuildStatusCheckedAt",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LatestCoreBuildChecksJson",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "LatestCoreBuildStatus",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "LatestCoreBuildStatusCheckedAt",
                table: "ManagedStacks");
        }
    }
}
