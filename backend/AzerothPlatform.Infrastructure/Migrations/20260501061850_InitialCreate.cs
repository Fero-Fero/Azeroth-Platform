using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManagedStacks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StackName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    NormalizedStackName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ServerType = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModuleIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DatabaseRootPassword = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DatabasePort = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthServerPort = table.Column<int>(type: "INTEGER", nullable: false),
                    WorldServerPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SoapPort = table.Column<int>(type: "INTEGER", nullable: false),
                    RealmName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    MaxPlayers = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomEnvVarsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CoreRepositoryUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CoreBranch = table.Column<string>(type: "TEXT", nullable: false),
                    CoreCommitSha = table.Column<string>(type: "TEXT", nullable: false),
                    LastBuiltAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModuleVersionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsOutdated = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCoreOutdated = table.Column<bool>(type: "INTEGER", nullable: false),
                    OutdatedModuleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LatestAvailableCoreSha = table.Column<string>(type: "TEXT", nullable: true),
                    OutdatedModulesJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastUpdateCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedStacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedStacks_NormalizedStackName",
                table: "ManagedStacks",
                column: "NormalizedStackName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManagedStacks");
        }
    }
}
