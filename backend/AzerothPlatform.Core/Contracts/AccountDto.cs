namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Account information from AzerothCore
/// </summary>
public class AccountDto
{
    /// <summary>
    /// Account ID
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// GM level (0 = player, 1-3 = GM levels)
    /// </summary>
    public int GmLevel { get; set; }
    
    /// <summary>
    /// Last login timestamp
    /// </summary>
    public DateTime? LastLogin { get; set; }
    
    /// <summary>
    /// Number of characters on this account
    /// </summary>
    public int CharacterCount { get; set; }
    
    /// <summary>
    /// Whether any character on this account is currently online
    /// </summary>
    public bool IsOnline { get; set; }
    
    /// <summary>
    /// Whether the account is currently banned
    /// </summary>
    public bool IsBanned { get; set; }
    
    /// <summary>
    /// Ban expiry date (null if permanent or not banned)
    /// </summary>
    public DateTime? BanExpiry { get; set; }
    
    /// <summary>
    /// Reason for the ban
    /// </summary>
    public string? BanReason { get; set; }
    
    /// <summary>
    /// Who banned the account
    /// </summary>
    public string? BannedBy { get; set; }
}

/// <summary>
/// Request to create a new account
/// </summary>
public class CreateAccountRequest
{
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// Account password
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Request to set GM level
/// </summary>
public class SetGmLevelRequest
{
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// GM level (0-3)
    /// </summary>
    public int Level { get; set; }
    
    /// <summary>
    /// Realm ID (-1 for all realms)
    /// </summary>
    public int RealmId { get; set; } = -1;
}

/// <summary>
/// Request to ban an account
/// </summary>
public class BanAccountRequest
{
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// Ban duration (e.g., "30m", "1h", "permanent")
    /// </summary>
    public string Duration { get; set; } = string.Empty;
    
    /// <summary>
    /// Reason for the ban
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request to delete an account
/// </summary>
public class DeleteAccountRequest
{
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
}

/// <summary>
/// Request to change account password
/// </summary>
public class SetPasswordRequest
{
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// New password
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Request to unban an account
/// </summary>
public class UnbanAccountRequest
{
    /// <summary>
    /// Account username
    /// </summary>
    public string Username { get; set; } = string.Empty;
}

/// <summary>
/// Request to unban an IP address
/// </summary>
public class UnbanIpRequest
{
    /// <summary>
    /// IP address to unban
    /// </summary>
    public string Ip { get; set; } = string.Empty;
}
