using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace AzerothPlatform.Tests;

/// <summary>
/// Custom WebApplicationFactory that wires up an in-memory SQLite database and
/// replaces external service dependencies (Docker, Git) with test doubles so
/// that the smoke-test suite can run without any real infrastructure.
///
/// SQLite in-memory databases are destroyed as soon as the last connection closes.
/// We prevent that by keeping a single, long-lived <see cref="SqliteConnection"/>
/// open for the lifetime of the factory.  All DbContext instances registered via
/// DI share that same connection so they all see the same schema and data.
/// </summary>
public sealed class AzerothPlatformWebApplicationFactory : WebApplicationFactory<Program>
{
    // Opened once in ConfigureWebHost and closed when the factory is disposed.
    private readonly SqliteConnection _keepAliveConnection;

    public AzerothPlatformWebApplicationFactory()
    {
        _keepAliveConnection = new SqliteConnection("Data Source=:memory:");
        _keepAliveConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Override configuration values that require real infrastructure.
        // Note: IWebHostBuilder.UseSetting uses ':' as the key-section separator.
        builder.UseSetting("Docker:SocketPath", "unix:///tmp/fake.sock");
        builder.UseSetting("Docker:BuildsPath", "/tmp/azp-test-builds");
        builder.UseSetting("StackUpdateChecker:CheckOnStartup", "false");
        builder.UseSetting("StackUpdateChecker:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            // ---- Replace EF Core DbContext registration ----
            // Remove every descriptor that relates to AzerothCoreDbContext so we
            // can wire in our persistent in-memory connection instead.
            RemoveService<DbContextOptions<AzerothCoreDbContext>>(services);
            RemoveService<AzerothCoreDbContext>(services);

            // Register the DbContext using the long-lived in-memory connection.
            // Because we reuse the same SqliteConnection, the database is never
            // destroyed between requests — all requests see the migrated schema.
            services.AddDbContext<AzerothCoreDbContext>(options =>
                options.UseSqlite(_keepAliveConnection));

            // ---- Replace IDockerService — Docker is not available in CI ----
            RemoveService<IDockerService>(services);

            var mockDocker = new Mock<IDockerService>();
            mockDocker
                .Setup(d => d.IsDockerAvailableAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            mockDocker
                .Setup(d => d.ListContainersAsync(
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    Array.Empty<AzerothPlatform.Core.Contracts.ContainerStatusDto>());
            services.AddScoped<IDockerService>(_ => mockDocker.Object);

            // ---- Replace IGitService — always reports Git as present ----
            RemoveService<IGitService>(services);

            var mockGit = new Mock<IGitService>();
            mockGit
                .Setup(g => g.IsGitAvailableAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            services.AddScoped<IGitService>(_ => mockGit.Object);

            // Override authentication so the deny-by-default policy is satisfied for functional tests.
            // The test handler authenticates every request as admin unless it carries the X-Anon header,
            // which lets the security tests assert that protected routes still 401 for anonymous callers.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAliveConnection.Dispose();
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }
    }
}
