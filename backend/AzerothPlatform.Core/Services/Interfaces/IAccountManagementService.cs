using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for managing AzerothCore accounts and characters
/// </summary>
public interface IAccountManagementService
{
    // Account queries (MySQL)
    Task<List<AccountDto>> GetAccountsAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a game account's credentials against the stack's auth database using SRP6 (the same
    /// salt/verifier scheme the game server uses). Returns success plus a player-facing error when it
    /// fails. Used by the launcher login screen; no password is ever stored or logged.
    /// </summary>
    Task<(bool Success, string? Error)> VerifyCredentialsAsync(string stackId, string username, string password, CancellationToken cancellationToken = default);
    Task<List<CharacterDto>> GetCharactersAsync(string stackId, int accountId, CancellationToken cancellationToken = default);
    Task<List<CharacterDto>> GetAllCharactersAsync(string stackId, CancellationToken cancellationToken = default);

    // Character inventory (MySQL)
    Task<CharacterInventoryDto> GetCharacterInventoryAsync(string stackId, int characterGuid, CancellationToken cancellationToken = default);

    // AH Bot setup (direct DB injection)
    Task<AhBotSetupResultDto> CreateAhBotCharactersAsync(string stackId, CancellationToken cancellationToken = default);
    
    // Account actions (SOAP)
    Task<(bool Success, string? Message)> CreateAccountAsync(string stackId, string username, string password, CancellationToken cancellationToken = default);
    Task<bool> DeleteAccountAsync(string stackId, string username, CancellationToken cancellationToken = default);
    Task<bool> SetPasswordAsync(string stackId, string username, string password, CancellationToken cancellationToken = default);
    Task<bool> SetGmLevelAsync(string stackId, string username, int level, int realmId = -1, CancellationToken cancellationToken = default);
    Task<bool> BanAccountAsync(string stackId, string username, string duration, string reason, CancellationToken cancellationToken = default);
    Task<bool> UnbanAccountAsync(string stackId, string username, CancellationToken cancellationToken = default);
    Task<bool> BanIpAsync(string stackId, string ip, string duration, string reason, CancellationToken cancellationToken = default);
    Task<bool> UnbanIpAsync(string stackId, string ip, CancellationToken cancellationToken = default);
    
    // Character actions (SOAP) — existing
    Task<bool> SendMessageAsync(string stackId, string characterName, string message, CancellationToken cancellationToken = default);
    Task<bool> SendItemsAsync(string stackId, string characterName, int itemId, int count, CancellationToken cancellationToken = default);
    Task<bool> SendMoneyAsync(string stackId, string characterName, long copperAmount, CancellationToken cancellationToken = default);
    Task<bool> KickPlayerAsync(string stackId, string characterName, string reason = "", CancellationToken cancellationToken = default);
    Task<bool> RenameCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default);
    Task<bool> CustomizeCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default);
    Task<bool> SetCharacterLevelAsync(string stackId, string characterName, int level, CancellationToken cancellationToken = default);

    // Character moderation (SOAP) — new
    Task<bool> BanCharacterAsync(string stackId, string characterName, string duration, string reason, CancellationToken cancellationToken = default);
    Task<bool> UnbanCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default);
    Task<bool> MuteCharacterAsync(string stackId, string characterName, int minutes, string reason, CancellationToken cancellationToken = default);
    Task<bool> UnmuteCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default);
    Task<bool> ReviveCharacterAsync(string stackId, string characterName, CancellationToken cancellationToken = default);

    // Character economy (SOAP) — new
    Task<bool> ModifyMoneyAsync(string stackId, string characterName, long copperAmount, CancellationToken cancellationToken = default);

    // Character items (SOAP) — new
    Task<bool> AddItemAsync(string stackId, string characterName, int itemId, int count, CancellationToken cancellationToken = default);
}

