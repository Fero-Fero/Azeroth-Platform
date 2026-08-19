using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Creates the <c>acore_armory</c> MySQL user with read-only access to game databases and minimal
/// write access: INSERT on <c>acore_auth.account</c> only (registration). Armory-owned extension
/// tables are created by root and receive scoped DML grants. The core <c>account</c> table never
/// receives UPDATE or DELETE grants - profile edits use <c>armory_account_profile</c> and are gated
/// in the armory app by a valid JWT session.
/// </summary>
public sealed class ArmoryDatabaseProvisioningService : IArmoryDatabaseProvisioningService
{
    public const string MysqlUsername = "acore_armory";

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IDockerService _dockerService;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<ArmoryDatabaseProvisioningService> _logger;

    public ArmoryDatabaseProvisioningService(
        AzerothCoreDbContext dbContext,
        IDockerService dockerService,
        IRemoteEngineService remoteEngine,
        ISecretProtector secretProtector,
        ILogger<ArmoryDatabaseProvisioningService> logger)
    {
        _dbContext = dbContext;
        _dockerService = dockerService;
        _remoteEngine = remoteEngine;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public string Username => MysqlUsername;

    public async Task EnsurePasswordAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await LoadStackAsync(stackId, cancellationToken);
        if (string.IsNullOrWhiteSpace(stack.ArmoryDatabasePasswordProtected))
        {
            stack.ArmoryDatabasePasswordProtected = _secretProtector.Protect(GeneratePassword());
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<(string User, string Password)> GetCredentialsAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        await EnsurePasswordAsync(stackId, cancellationToken);
        var stack = await LoadStackAsync(stackId, cancellationToken);
        return (MysqlUsername, _secretProtector.Unprotect(stack.ArmoryDatabasePasswordProtected));
    }

    public async Task EnsureProvisionedAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await LoadStackAsync(stackId, cancellationToken);
        if (!stack.IncludeArmory || !stack.ArmoryEnabled)
        {
            return;
        }

        await EnsurePasswordAsync(stackId, cancellationToken);
        stack = await LoadStackAsync(stackId, cancellationToken);
        var password = _secretProtector.Unprotect(stack.ArmoryDatabasePasswordProtected);
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("Armory DB password missing for stack {StackId}; skipping MySQL provisioning.", stackId);
            return;
        }

        try
        {
            var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stack.Id);
            var dockerContext = stack.DeploymentTarget == DeploymentTarget.External
                ? await _remoteEngine.EnsureContextAsync(stack, cancellationToken)
                : null;

            var containers = await _dockerService.ListContainersAsync(
                composeProjectName,
                dockerContext,
                cancellationToken: cancellationToken);
            var databaseContainer = containers
                .FirstOrDefault(c => c.Name.Contains("database", StringComparison.OrdinalIgnoreCase));

            if (databaseContainer is null)
            {
                _logger.LogWarning("Database container not found for stack {StackId}; skipping armory DB provisioning.", stackId);
                return;
            }

            var sql = BuildProvisioningSql(password);
            var contextArg = dockerContext is null ? string.Empty : $"--context {dockerContext} ";
            var arguments =
                $"{contextArg}exec -i {databaseContainer.Name} mysql -uroot " +
                $"-p{stack.DatabaseRootPassword} -e \"{sql.Replace("\"", "\\\"")}\"";

            var (exitCode, _, stderr) = await RunDockerCliAsync(arguments, cancellationToken);
            if (exitCode != 0)
            {
                var actualError = FilterMysqlCliNoise(stderr);
                _logger.LogWarning(
                    "Armory DB provisioning for stack {StackId} exited {ExitCode}: {Error}",
                    stackId,
                    exitCode,
                    actualError);
                return;
            }

            _logger.LogInformation(
                "Provisioned MySQL user {User} for armory on stack {StackId} (read-only game DBs; INSERT-only on account).",
                MysqlUsername,
                stackId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Armory DB provisioning failed for stack {StackId}.", stackId);
        }
    }

    private async Task<ManagedStackEntity> LoadStackAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            throw new InvalidOperationException($"Stack '{stackId}' not found.");
        }

        return stack;
    }

    internal static string BuildProvisioningSql(string armoryPassword)
    {
        var escapedPassword = EscapeSqlLiteral(armoryPassword);
        var sb = new StringBuilder();

        sb.AppendLine($"CREATE USER IF NOT EXISTS '{MysqlUsername}'@'%' IDENTIFIED BY '{escapedPassword}';");
        sb.AppendLine($"ALTER USER '{MysqlUsername}'@'%' IDENTIFIED BY '{escapedPassword}';");
        sb.AppendLine($"REVOKE ALL PRIVILEGES, GRANT OPTION FROM '{MysqlUsername}'@'%';");

        sb.AppendLine($"GRANT SELECT ON acore_world.* TO '{MysqlUsername}'@'%';");
        sb.AppendLine($"GRANT SELECT ON acore_characters.* TO '{MysqlUsername}'@'%';");
        sb.AppendLine($"GRANT SELECT ON acore_auth.account TO '{MysqlUsername}'@'%';");
        sb.AppendLine($"GRANT SELECT ON acore_auth.account_access TO '{MysqlUsername}'@'%';");
        sb.AppendLine($"GRANT SELECT ON acore_auth.uptime TO '{MysqlUsername}'@'%';");
        sb.AppendLine($"GRANT INSERT ON acore_auth.account TO '{MysqlUsername}'@'%';");

        sb.AppendLine("""
            CREATE TABLE IF NOT EXISTS acore_auth.armory_pending_registration (
                id INT UNSIGNED NOT NULL AUTO_INCREMENT,
                email VARCHAR(255) NOT NULL,
                salt BINARY(32) NOT NULL,
                verifier BINARY(32) NOT NULL,
                verification_token_hash VARCHAR(64) NOT NULL,
                expires_at DATETIME NOT NULL,
                created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                verified_at DATETIME NULL DEFAULT NULL,
                account_id INT UNSIGNED NULL DEFAULT NULL,
                resend_count TINYINT UNSIGNED NOT NULL DEFAULT 0,
                resend_window_started_at DATETIME NULL DEFAULT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_armory_pending_email (email)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """);

        sb.AppendLine("""
            CREATE TABLE IF NOT EXISTS acore_auth.armory_account_profile (
                account_id INT UNSIGNED NOT NULL,
                display_name VARCHAR(32) NOT NULL,
                hide_username TINYINT(1) NOT NULL DEFAULT 1,
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (account_id),
                UNIQUE KEY ux_armory_display_name (display_name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """);

        sb.AppendLine($"GRANT SELECT, INSERT, UPDATE, DELETE ON acore_auth.armory_pending_registration TO '{MysqlUsername}'@'%';");
        sb.AppendLine($"GRANT SELECT, INSERT, UPDATE ON acore_auth.armory_account_profile TO '{MysqlUsername}'@'%';");
        sb.AppendLine("FLUSH PRIVILEGES;");

        return sb.ToString().Replace("\r\n", " ").Replace("\n", " ");
    }

    private static string EscapeSqlLiteral(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''");

    private static string FilterMysqlCliNoise(string? stderr) =>
        string.Join("\n", (stderr ?? string.Empty)
            .Split('\n')
            .Where(line => !line.Contains("Using a password on the command line", StringComparison.OrdinalIgnoreCase)));

    private static string GeneratePassword(int length = 32)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var password = new char[length];
        for (var i = 0; i < length; i++)
        {
            password[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }

        return new string(password);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCliAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout, stderr);
    }
}
