using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudAuditLogs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudAuditLogs_OccurredAtUtc",
                table: "CloudAuditLogs",
                column: "OccurredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudAuditLogs");
        }
    }
}
