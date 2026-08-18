using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Helpers;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Service for managing AzerothCore accounts and characters
/// </summary>
public class AccountManagementService : IAccountManagementService
{
    private readonly ISoapProxyService _soapProxy;
    private readonly IMySqlConnectionFactory _connectionFactory;
    private readonly ILogger<AccountManagementService> _logger;

    public AccountManagementService(
        ISoapProxyService soapProxy,
        IMySqlConnectionFactory connectionFactory,
        ILogger<AccountManagementService> logger)
    {
        _soapProxy = soapProxy;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    #region Account Queries (MySQL)

    public async Task<List<AccountDto>> GetAccountsAsync(string stackId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);

        var sql = @"
            SELECT 
                a.id AS Id,
                a.username AS Username,
                COALESCE(aa.gmlevel, 0) AS GmLevel,
                a.last_login AS LastLogin,
                COUNT(DISTINCT c.guid) AS CharacterCount,
                MAX(c.online) AS IsOnline,
                COALESCE(ab.active, 0) AS IsBanned,
                FROM_UNIXTIME(ab.unbandate) AS BanExpiry,
                ab.banreason AS BanReason,
                ab.bannedby AS BannedBy
            FROM account a
            LEFT JOIN account_access aa ON a.id = aa.id AND aa.RealmID = -1
            LEFT JOIN acore_characters.characters c ON c.account = a.id
            LEFT JOIN account_banned ab ON ab.id = a.id AND ab.active = 1
            GROUP BY a.id, a.username, aa.gmlevel, a.last_login, ab.active, ab.unbandate, ab.banreason, ab.bannedby
            ORDER BY a.id";

        var accounts = await connection.QueryAsync<AccountDto>(sql);
        return accounts.ToList();
    }

    public async Task<(bool Success, string? Error)> VerifyCredentialsAsync(string stackId, string username, string password, CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (username.Length is < 1 or > 16 || password.Length is < 1 or > 16)
        {
            // Same shape as a wrong password so we never reveal which accounts exist.
            return (false, "Invalid username or password.");
        }

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);

            // AzerothCore stores account names uppercased; match that so login is case-insensitive.
            var row = await connection.QuerySingleOrDefaultAsync<AccountCredentialRow>(
                "SELECT salt AS Salt, verifier AS Verifier FROM account WHERE username = @Username",
                new { Username = username.ToUpperInvariant() });

            if (row?.Salt is null || row.Verifier is null)
            {
                return (false, "Invalid username or password.");
            }

            var valid = SrpHelper.VerifyPassword(username, password, row.Salt, row.Verifier);
            if (!valid)
            {
                return (false, "Invalid username or password.");
            }

            // Block banned accounts from logging in through the launcher.
            var isBanned = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM account_banned ab JOIN account a ON a.id = ab.id WHERE a.username = @Username AND ab.active = 1",
                new { Username = username.ToUpperInvariant() });
            if (isBanned > 0)
            {
                return (false, "This account is banned.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying credentials for account {Username} on stack {StackId}", username, stackId);
            return (false, "Could not reach the login server. Please try again.");
        }
    }

    private sealed class AccountCredentialRow
    {
        public byte[]? Salt { get; set; }
        public byte[]? Verifier { get; set; }
    }

    public async Task<List<CharacterDto>> GetCharactersAsync(string stackId, int accountId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "characters", cancellationToken);

        var sql = @"
            SELECT 
                c.guid AS Guid,
                c.name AS Name,
                c.account AS Account,
                c.level AS Level,
                c.race AS Race,
                c.class AS Class,
                c.gender AS Gender,
                c.online AS Online,
                c.totaltime AS TotalTime,
                c.map AS Map,
                c.zone AS Zone,
                c.money AS Money,
                c.position_x AS PositionX,
                c.position_y AS PositionY,
                c.position_z AS PositionZ,
                g.name AS Guild
            FROM characters c
            LEFT JOIN guild_member gm ON gm.guid = c.guid
            LEFT JOIN guild g ON g.guildid = gm.guildid
            WHERE c.account = @AccountId
            ORDER BY c.level DESC, c.totaltime DESC";

        var characters = await connection.QueryAsync<CharacterDto>(sql, new { AccountId = accountId });
        return characters.ToList();
    }

    public async Task<List<CharacterDto>> GetAllCharactersAsync(string stackId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "characters", cancellationToken);

        var sql = @"
            SELECT 
                c.guid AS Guid,
                c.name AS Name,
                c.account AS Account,
                a.username AS AccountUsername,
                c.level AS Level,
                c.race AS Race,
                c.class AS Class,
                c.gender AS Gender,
                c.online AS Online,
                c.totaltime AS TotalTime,
                c.map AS Map,
                c.zone AS Zone,
                c.money AS Money,
                c.position_x AS PositionX,
                c.position_y AS PositionY,
                c.position_z AS PositionZ,
                g.name AS Guild
            FROM characters c
            LEFT JOIN acore_auth.account a ON c.account = a.id
            LEFT JOIN guild_member gm ON gm.guid = c.guid
            LEFT JOIN guild g ON g.guildid = gm.guildid
            ORDER BY c.name ASC";

        var characters = await connection.QueryAsync<CharacterDto>(sql);
        return characters.ToList();
    }

    #endregion

    // ===== SOAP argument sanitization =====

    /// <summary>
    /// Validates an identifier token (account/character name, IP) that occupies a single position in a
    /// space-delimited SOAP command. Rejects empty values and any whitespace/control characters so a
    /// crafted value cannot inject extra command tokens. Returns the trimmed value.
    /// </summary>
    private static string RequireToken(string? value, string field)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
        {
            throw new ArgumentException($"{field} must be a single token with no spaces or control characters.");
        }

        return trimmed;
    }

    /// <summary>
    /// Cleans a free-text SOAP argument (ban/kick/mute reason, broadcast message) that is expected to be
    /// the trailing token(s) of the command. Strips control characters (including CR/LF, which could add a
    /// second command line) and collapses them to spaces.
    /// </summary>
    private static string CleanText(string? value)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    }

    #region Account Actions (SOAP)

    public async Task<(bool Success, string? Message)> CreateAccountAsync(string stackId, string username, string password, CancellationToken cancellationToken = default)
    {
        // AzerothCore account requirements enforced up front so the user gets a clear reason instead of
        // a generic SOAP failure. The account name and password are each limited to 16 characters, and
        // the SOAP command is space-delimited so whitespace would break parsing.
        username = username.Trim();
        if (username.Length is < 1 or > 16)
        {
            return (false, "Username must be between 1 and 16 characters.");
        }
        if (password.Length is < 1 or > 16)
        {
            return (false, "Password must be between 1 and 16 characters.");
        }
        if (username.Any(char.IsWhiteSpace) || password.Any(char.IsWhiteSpace))
        {
            return (false, "Username and password cannot contain spaces.");
        }

        try
        {
            var command = $"account create {username} {password}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            var success = result.Contains("created", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Account {Username} created successfully on stack {StackId}", username, stackId);
                return (true, null);
            }

            _logger.LogWarning("Failed to create account {Username} on stack {StackId}: {Result}", username, stackId, result);
            var message = string.IsNullOrWhiteSpace(result) ? "The world server rejected the account creation." : result.Trim();
            return (false, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating account {Username} on stack {StackId}", username, stackId);
            return (false, ex.Message);
        }
    }

    public async Task<bool> SetGmLevelAsync(string stackId, string username, int level, int realmId = -1, CancellationToken cancellationToken = default)
    {
        try
        {
            username = RequireToken(username, "Username");
            var command = $"account set gmlevel {username} {level} {realmId}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("change", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("success", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("GM level for account {Username} set to {Level} on stack {StackId}", username, level, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to set GM level for account {Username} on stack {StackId}: {Result}", username, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting GM level for account {Username} on stack {StackId}", username, stackId);
            return false;
        }
    }

    public async Task<bool> BanAccountAsync(string stackId, string username, string duration, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            username = RequireToken(username, "Username");
            duration = RequireToken(duration, "Duration");
            reason = CleanText(reason);
            var command = $"ban account {username} {duration} {reason}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("banned", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Account {Username} banned on stack {StackId} for {Duration}: {Reason}", username, stackId, duration, reason);
            }
            else
            {
                _logger.LogWarning("Failed to ban account {Username} on stack {StackId}: {Result}", username, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error banning account {Username} on stack {StackId}", username, stackId);
            return false;
        }
    }

    public async Task<bool> BanIpAsync(string stackId, string ip, string duration, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            ip = RequireToken(ip, "IP");
            duration = RequireToken(duration, "Duration");
            reason = CleanText(reason);
            var command = $"ban ip {ip} {duration} {reason}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("banned", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("IP {Ip} banned on stack {StackId} for {Duration}: {Reason}", ip, stackId, duration, reason);
            }
            else
            {
                _logger.LogWarning("Failed to ban IP {Ip} on stack {StackId}: {Result}", ip, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error banning IP {Ip} on stack {StackId}", ip, stackId);
            return false;
        }
    }

    public async Task<bool> DeleteAccountAsync(string stackId, string username, CancellationToken cancellationToken = default)
    {
        try
        {
            username = RequireToken(username, "Username");
            var command = $"account delete {username}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("removed", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Account {Username} deleted on stack {StackId}", username, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to delete account {Username} on stack {StackId}: {Result}", username, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting account {Username} on stack {StackId}", username, stackId);
            return false;
        }
    }

    public async Task<bool> SetPasswordAsync(string stackId, string username, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            username = RequireToken(username, "Username");
            password = RequireToken(password, "Password");
            var command = $"account set password {username} {password} {password}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("changed", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("password", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Password changed for account {Username} on stack {StackId}", username, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to change password for account {Username} on stack {StackId}: {Result}", username, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for account {Username} on stack {StackId}", username, stackId);
            return false;
        }
    }

    public async Task<bool> UnbanAccountAsync(string stackId, string username, CancellationToken cancellationToken = default)
    {
        try
        {
            username = RequireToken(username, "Username");
            var command = $"unban account {username}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("unbanned", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("removed", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Account {Username} unbanned on stack {StackId}", username, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to unban account {Username} on stack {StackId}: {Result}", username, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unbanning account {Username} on stack {StackId}", username, stackId);
            return false;
        }
    }

    public async Task<bool> UnbanIpAsync(string stackId, string ip, CancellationToken cancellationToken = default)
    {
        try
        {
            ip = RequireToken(ip, "IP");
            var command = $"unban ip {ip}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("unbanned", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("removed", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("IP {Ip} unbanned on stack {StackId}", ip, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to unban IP {Ip} on stack {StackId}: {Result}", ip, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unbanning IP {Ip} on stack {StackId}", ip, stackId);
            return false;
        }
    }

    #endregion

    #region Character Actions (SOAP)

    public async Task<bool> SendMessageAsync(string stackId, string characterName, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            message = CleanText(message);
            var command = $"send message {characterName} {message}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = !result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                         !result.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Message sent to character {CharacterName} on stack {StackId}", characterName, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to send message to character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> SendItemsAsync(string stackId, string characterName, int itemId, int count, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"send items {characterName} \"Items from Manager\" \"Items sent via Azeroth Platform\" {itemId}:{count}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("Mail sent", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Items sent to character {CharacterName} on stack {StackId}: {ItemId}x{Count}", characterName, stackId, itemId, count);
            }
            else
            {
                _logger.LogWarning("Failed to send items to character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending items to character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> SendMoneyAsync(string stackId, string characterName, long copperAmount, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"send money {characterName} \"Gold from Manager\" \"Gold sent via Azeroth Platform\" {copperAmount}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("Mail sent", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Money sent to character {CharacterName} on stack {StackId}: {Copper} copper", characterName, stackId, copperAmount);
            }
            else
            {
                _logger.LogWarning("Failed to send money to character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending money to character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> KickPlayerAsync(string stackId, string characterName, string reason = "", CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            reason = CleanText(reason);
            var command = string.IsNullOrWhiteSpace(reason)
                ? $"kick {characterName}"
                : $"kick {characterName} {reason}";
            
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            // AC may return an empty string on success for kick; use negative detection.
            var success = !result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("offline", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Character {CharacterName} kicked from stack {StackId}", characterName, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to kick character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error kicking character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> RenameCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"character rename {characterName}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("rename", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                         (!result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                          string.IsNullOrWhiteSpace(result));

            if (success)
            {
                _logger.LogInformation("Character {CharacterName} marked for rename on stack {StackId}", characterName, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to mark character {CharacterName} for rename on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking character {CharacterName} for rename on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> CustomizeCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"character customize {characterName}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("customize", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                         (!result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                          string.IsNullOrWhiteSpace(result));

            if (success)
            {
                _logger.LogInformation("Character {CharacterName} marked for customization on stack {StackId}", characterName, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to mark character {CharacterName} for customization on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking character {CharacterName} for customization on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> SetCharacterLevelAsync(string stackId, string characterName, int level, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"character level {characterName} {level}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);
            
            var success = result.Contains("level", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                         result.Contains("changed", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _logger.LogInformation("Character {CharacterName} level set to {Level} on stack {StackId}", characterName, level, stackId);
            }
            else
            {
                _logger.LogWarning("Failed to set character {CharacterName} level on stack {StackId}: {Result}", characterName, stackId, result);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting character {CharacterName} level on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> BanCharacterAsync(string stackId, string characterName, string duration, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            duration = RequireToken(duration, "Duration");
            reason = CleanText(reason);
            var command = $"ban character {characterName} {duration} {reason}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            // "ban" alone is too broad - it matches error messages like "cannot ban"
            var success = result.Contains("banned", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (success)
                _logger.LogInformation("Character {CharacterName} banned on stack {StackId} for {Duration}: {Reason}", characterName, stackId, duration, reason);
            else
                _logger.LogWarning("Failed to ban character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error banning character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> UnbanCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"unban character {characterName}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            // AC returns empty string on success, only sends error message if player not found
            var success = !result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (success)
                _logger.LogInformation("Character {CharacterName} unbanned on stack {StackId}", characterName, stackId);
            else
                _logger.LogWarning("Failed to unban character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unbanning character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> MuteCharacterAsync(string stackId, string characterName, int minutes, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            reason = CleanText(reason);
            var command = $"mute {characterName} {minutes} {reason}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            // AC sends mute notifications to online GMs, not the console/SOAP caller - empty response = success
            var success = !result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (success)
                _logger.LogInformation("Character {CharacterName} muted for {Minutes}m on stack {StackId}: {Reason}", characterName, minutes, stackId, reason);
            else
                _logger.LogWarning("Failed to mute character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error muting character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> UnmuteCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"unmute {characterName}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            // AC returns "You have enabled {}'s chat." on success, or "Player's chat is already enabled." - both are fine
            var success = result.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                          (!result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                           !result.Contains("error", StringComparison.OrdinalIgnoreCase));

            if (success)
                _logger.LogInformation("Character {CharacterName} unmuted on stack {StackId}", characterName, stackId);
            else
                _logger.LogWarning("Failed to unmute character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unmuting character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> ReviveCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"revive {characterName}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            var success = !result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                          !result.Contains("not found", StringComparison.OrdinalIgnoreCase);

            if (success)
                _logger.LogInformation("Character {CharacterName} revived on stack {StackId}", characterName, stackId);
            else
                _logger.LogWarning("Failed to revive character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviving character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> ModifyMoneyAsync(string stackId, string characterName, long copperAmount, CancellationToken cancellationToken = default)
    {
        try
        {
            if (copperAmount <= 0)
            {
                // .modify money #money requires a selected target in-game; it cannot remove gold via SOAP.
                _logger.LogWarning("Cannot remove money from character {CharacterName} via SOAP - .modify money requires a selected target", characterName);
                return false;
            }

            // .modify money is target-based (no player name param). For SOAP, the only viable
            // approach to deliver gold to a specific character is via mail (send money).
            characterName = RequireToken(characterName, "Character name");
            var command = $"send money {characterName} \"Gold from Manager\" \"Gold sent via Azeroth Platform\" {copperAmount}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            var success = result.Contains("Mail sent", StringComparison.OrdinalIgnoreCase) ||
                          (!result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                           !result.Contains("not found", StringComparison.OrdinalIgnoreCase));

            if (success)
                _logger.LogInformation("Sent {Amount} copper to character {CharacterName} via mail on stack {StackId}", copperAmount, characterName, stackId);
            else
                _logger.LogWarning("Failed to modify money for character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error modifying money for character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    public async Task<bool> AddItemAsync(string stackId, string characterName, int itemId, int count, CancellationToken cancellationToken = default)
    {
        try
        {
            characterName = RequireToken(characterName, "Character name");
            var command = $"additem {characterName} {itemId} {count}";
            var result = await _soapProxy.ExecuteCommandAsync(stackId, command, cancellationToken);

            // AC responds with "You have added N of item #X to Y's inventory." - "added" is reliable.
            var success = result.Contains("added", StringComparison.OrdinalIgnoreCase) ||
                          (!result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                           !result.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                           string.IsNullOrWhiteSpace(result));

            if (success)
                _logger.LogInformation("Added item {ItemId}x{Count} to character {CharacterName} on stack {StackId}", itemId, count, characterName, stackId);
            else
                _logger.LogWarning("Failed to add item to character {CharacterName} on stack {StackId}: {Result}", characterName, stackId, result);

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding item to character {CharacterName} on stack {StackId}", characterName, stackId);
            return false;
        }
    }

    #endregion
    
    #region Character Inventory (MySQL)

    public async Task<CharacterInventoryDto> GetCharacterInventoryAsync(string stackId, int characterGuid, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "characters", cancellationToken);

        // Query all item_instance rows for this character, joined to item_template for display data.
        // bag = 0 means item is directly on the character. For bag items, bag = the item_guid of the container.
        var sql = @"
            SELECT
                ci.bag       AS Bag,
                ci.slot      AS Slot,
                ci.item      AS ItemGuid,
                ii.itemEntry AS ItemEntry,
                it.name      AS ItemName,
                it.displayid AS DisplayId,
                it.Quality   AS Quality,
                it.ItemLevel AS ItemLevel,
                it.RequiredLevel AS RequiredLevel,
                ii.count     AS StackCount,
                ii.durability    AS Durability,
                it.MaxDurability AS MaxDurability
            FROM character_inventory ci
            INNER JOIN item_instance ii ON ii.guid = ci.item
            INNER JOIN acore_world.item_template it ON it.entry = ii.itemEntry
            WHERE ci.guid = @CharacterGuid
            ORDER BY ci.bag ASC, ci.slot ASC";

        var rawItems = (await connection.QueryAsync<RawInventoryRow>(sql, new { CharacterGuid = characterGuid })).ToList();

        var result = new CharacterInventoryDto();

        // Build a lookup of container guids → ContainerSlot (slots 19–22 and 67–74 on bag=0)
        var containersByGuid = rawItems
            .Where(r => r.Bag == 0 && r.Slot >= 19 && r.Slot <= 22)
            .ToDictionary(r => r.ItemGuid, r => r);

        var bankContainersByGuid = rawItems
            .Where(r => r.Bag == 0 && r.Slot >= 67 && r.Slot <= 74)
            .ToDictionary(r => r.ItemGuid, r => r);

        foreach (var row in rawItems)
        {
            var slot = MapRow(row);

            if (row.Bag == 0)
            {
                if (row.Slot <= 18)
                    result.EquippedItems.Add(slot);
                else if (row.Slot >= 19 && row.Slot <= 22)
                    EnsureBag(result.BagItems, row, slot);   // container itself - will be created by EnsureBag
                else if (row.Slot >= 23 && row.Slot <= 38)
                    result.BackpackItems.Add(slot);
                else if (row.Slot >= 39 && row.Slot <= 66)
                    result.BankItems.Add(slot);
                else if (row.Slot >= 67 && row.Slot <= 74)
                    EnsureBankBag(result.BankBagItems, row, slot);
            }
            else if (containersByGuid.TryGetValue(row.Bag, out var containerRow))
            {
                var bag = result.BagItems.FirstOrDefault(b => b.ContainerGuid == row.Bag);
                bag?.Items.Add(slot);
            }
            else if (bankContainersByGuid.TryGetValue(row.Bag, out var bankContainerRow))
            {
                var bag = result.BankBagItems.FirstOrDefault(b => b.ContainerGuid == row.Bag);
                bag?.Items.Add(slot);
            }
        }

        return result;
    }

    private static ItemSlotDto MapRow(RawInventoryRow row) => new()
    {
        Bag = row.Bag,
        Slot = row.Slot,
        ItemGuid = row.ItemGuid,
        ItemEntry = row.ItemEntry,
        ItemName = row.ItemName,
        DisplayId = row.DisplayId,
        Quality = row.Quality,
        ItemLevel = row.ItemLevel,
        RequiredLevel = row.RequiredLevel,
        StackCount = row.StackCount,
        Durability = row.Durability,
        MaxDurability = row.MaxDurability
    };

    private static void EnsureBag(List<BagDto> bags, RawInventoryRow containerRow, ItemSlotDto slot)
    {
        if (!bags.Any(b => b.ContainerGuid == containerRow.ItemGuid))
        {
            bags.Add(new BagDto
            {
                ContainerSlot = containerRow.Slot,
                ContainerGuid = containerRow.ItemGuid,
                ContainerEntry = containerRow.ItemEntry,
                ContainerName = containerRow.ItemName,
                Items = []
            });
        }
    }

    private static void EnsureBankBag(List<BagDto> bags, RawInventoryRow containerRow, ItemSlotDto slot)
    {
        if (!bags.Any(b => b.ContainerGuid == containerRow.ItemGuid))
        {
            bags.Add(new BagDto
            {
                ContainerSlot = containerRow.Slot,
                ContainerGuid = containerRow.ItemGuid,
                ContainerEntry = containerRow.ItemEntry,
                ContainerName = containerRow.ItemName,
                Items = []
            });
        }
    }

    private sealed class RawInventoryRow
    {
        public int Bag { get; set; }
        public int Slot { get; set; }
        public int ItemGuid { get; set; }
        public int ItemEntry { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public int DisplayId { get; set; }
        public int Quality { get; set; }
        public int ItemLevel { get; set; }
        public int RequiredLevel { get; set; }
        public int StackCount { get; set; }
        public int Durability { get; set; }
        public int MaxDurability { get; set; }
    }

    #endregion

    #region AH Bot Setup

    public async Task<AhBotSetupResultDto> CreateAhBotCharactersAsync(string stackId, CancellationToken cancellationToken = default)
    {
        const string AhBotUsername = "AHBOT";
        const string AllianceCharName = "AhBotHuman";
        const string HordeCharName = "AhBotOrc";
        const int Money = 10_000_000; // 1000 gold in copper

        _logger.LogInformation("Creating AH Bot account and characters for stack {StackId}", stackId);

        // Step 1: Create AHBOT account in auth DB (idempotent via INSERT IGNORE)
        var (saltHex, verifierHex) = SrpHelper.CalculateCredentials(AhBotUsername, AhBotUsername);
        using var authConn = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);

        await authConn.ExecuteAsync(
            "INSERT IGNORE INTO account (username, salt, verifier, expansion) VALUES (@Username, UNHEX(@Salt), UNHEX(@Verifier), 2)",
            new { Username = AhBotUsername, Salt = saltHex, Verifier = verifierHex });

        var accountId = await authConn.ExecuteScalarAsync<int>(
            "SELECT id FROM account WHERE username = @Username",
            new { Username = AhBotUsername });

        // Step 2: Check for existing bot characters
        using var charConn = await _connectionFactory.CreateConnectionAsync(stackId, "characters", cancellationToken);

        var existingAllianceGuid = await charConn.ExecuteScalarAsync<int?>(
            "SELECT guid FROM characters WHERE name = @Name AND account = @AccountId",
            new { Name = AllianceCharName, AccountId = accountId });

        var existingHordeGuid = await charConn.ExecuteScalarAsync<int?>(
            "SELECT guid FROM characters WHERE name = @Name AND account = @AccountId",
            new { Name = HordeCharName, AccountId = accountId });

        if (existingAllianceGuid.HasValue && existingHordeGuid.HasValue)
        {
            _logger.LogInformation("AH Bot characters already exist for stack {StackId}: Alliance={AllianceGuid}, Horde={HordeGuid}",
                stackId, existingAllianceGuid.Value, existingHordeGuid.Value);
            return new AhBotSetupResultDto(accountId, existingAllianceGuid.Value, existingHordeGuid.Value, CharactersCreated: false);
        }

        // Step 3: Assign GUIDs and insert missing characters
        var maxGuid = await charConn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(guid), 0) FROM characters");

        var allianceGuid = existingAllianceGuid ?? (maxGuid + 1);
        var hordeGuid = existingHordeGuid ?? (existingAllianceGuid.HasValue ? maxGuid + 1 : maxGuid + 2);

        if (!existingAllianceGuid.HasValue)
        {
            // Human Warrior - starts in Northshire Valley (Map 0, Zone 12)
            await charConn.ExecuteAsync(@"
                INSERT INTO characters
                    (guid, account, name, race, class, gender, level, money,
                     map, zone, position_x, position_y, position_z, orientation,
                     taximask, innTriggerId, health, power1)
                VALUES
                    (@Guid, @AccountId, @Name, 1, 1, 0, 1, @Money,
                     0, 12, -8949.95, -132.493, 83.5312, 0,
                     '', 0, 100, 0)",
                new { Guid = allianceGuid, AccountId = accountId, Name = AllianceCharName, Money });
        }

        if (!existingHordeGuid.HasValue)
        {
            // Orc Warrior - starts in Valley of Trials (Map 1, Zone 14)
            await charConn.ExecuteAsync(@"
                INSERT INTO characters
                    (guid, account, name, race, class, gender, level, money,
                     map, zone, position_x, position_y, position_z, orientation,
                     taximask, innTriggerId, health, power1)
                VALUES
                    (@Guid, @AccountId, @Name, 2, 1, 0, 1, @Money,
                     1, 14, -618.518, -4251.67, 38.718, 0,
                     '', 0, 100, 0)",
                new { Guid = hordeGuid, AccountId = accountId, Name = HordeCharName, Money });
        }

        _logger.LogInformation("AH Bot characters created for stack {StackId}: Alliance GUID={AllianceGuid}, Horde GUID={HordeGuid}",
            stackId, allianceGuid, hordeGuid);

        return new AhBotSetupResultDto(accountId, allianceGuid, hordeGuid, CharactersCreated: true);
    }

    #endregion
}
