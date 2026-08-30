using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogModules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Repository = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RequiresPlayerbots = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogModules", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogModules");
        }
    }
}
