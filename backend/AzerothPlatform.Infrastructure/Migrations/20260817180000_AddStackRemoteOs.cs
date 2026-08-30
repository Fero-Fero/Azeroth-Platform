using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AzerothCoreDbContext))]
    [Migration("20260817180000_AddStackRemoteOs")]
    public partial class AddStackRemoteOs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RemoteOs",
                table: "ManagedStacks",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Linux");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemoteOs",
                table: "ManagedStacks");
        }
    }
}
