using Dapper;
using MySql.Data.MySqlClient;

namespace AzerothPlatform.ClientServer;

/// <summary>
/// Verifies player credentials against this stack's own auth MySQL database over the compose network,
/// so a launcher logs in directly to the stack (no manager in the path). Mirrors the manager's
/// AccountManagementService.VerifyCredentialsAsync SRP6 check.
/// </summary>
public sealed class LoginService
{
    private readonly ClientContentOptions _options;
    private readonly ILogger<LoginService> _logger;

    public LoginService(ClientContentOptions options, ILogger<LoginService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.LoginEnabled;

    public async Task<(bool Success, string? Error)> VerifyAsync(string username, string password, CancellationToken cancellationToken)
    {
        username = (username ?? string.Empty).Trim();
        password ??= string.Empty;

        // Same shape as a wrong password so we never reveal which accounts exist.
        if (username.Length is < 1 or > 16 || password.Length is < 1 or > 16)
        {
            return (false, "Invalid username or password.");
        }

        try
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = _options.DbHost,
                Port = (uint)_options.DbPort,
                Database = _options.AuthDatabase,
                UserID = _options.DbUser,
                Password = _options.DbPassword,
                AllowPublicKeyRetrieval = true,
            };

            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            // AzerothCore stores account names uppercased; match that so login is case-insensitive.
            var upper = username.ToUpperInvariant();
            var row = await connection.QuerySingleOrDefaultAsync<AccountCredentialRow>(
                "SELECT salt AS Salt, verifier AS Verifier FROM account WHERE username = @Username",
                new { Username = upper });

            if (row?.Salt is null || row.Verifier is null
                || !SrpHelper.VerifyPassword(username, password, row.Salt, row.Verifier))
            {
                return (false, "Invalid username or password.");
            }

            var isBanned = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM account_banned ab JOIN account a ON a.id = ab.id WHERE a.username = @Username AND ab.active = 1",
                new { Username = upper });
            if (isBanned > 0)
            {
                return (false, "This account is banned.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying credentials for account {Username}.", username);
            return (false, "Could not reach the login server. Please try again.");
        }
    }

    private sealed class AccountCredentialRow
    {
        public byte[]? Salt { get; set; }
        public byte[]? Verifier { get; set; }
    }
}
