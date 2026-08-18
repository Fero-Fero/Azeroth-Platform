namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Character information from AzerothCore
/// </summary>
public class CharacterDto
{
    /// <summary>
    /// Character GUID
    /// </summary>
    public int Guid { get; set; }
    
    /// <summary>
    /// Character name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Account ID
    /// </summary>
    public int Account { get; set; }
    
    /// <summary>
    /// Account username (populated when querying all characters)
    /// </summary>
    public string? AccountUsername { get; set; }
    
    /// <summary>
    /// Character level
    /// </summary>
    public int Level { get; set; }
    
    /// <summary>
    /// Race ID
    /// </summary>
    public int Race { get; set; }
    
    /// <summary>
    /// Class ID
    /// </summary>
    public int Class { get; set; }
    
    /// <summary>
    /// Gender (0 = male, 1 = female)
    /// </summary>
    public int Gender { get; set; }
    
    /// <summary>
    /// Whether the character is currently online
    /// </summary>
    public bool Online { get; set; }
    
    /// <summary>
    /// Total playtime in seconds
    /// </summary>
    public int TotalTime { get; set; }
    
    /// <summary>
    /// Current map ID
    /// </summary>
    public int Map { get; set; }
    
    /// <summary>
    /// X coordinate
    /// </summary>
    public float PositionX { get; set; }
    
    /// <summary>
    /// Y coordinate
    /// </summary>
    public float PositionY { get; set; }
    
    /// <summary>
    /// Z coordinate
    /// </summary>
    public float PositionZ { get; set; }

    /// <summary>
    /// Character wealth in copper (1 gold = 10 000 copper)
    /// </summary>
    public long Money { get; set; }

    /// <summary>
    /// Zone ID the character is currently in
    /// </summary>
    public int Zone { get; set; }

    /// <summary>
    /// Guild name (null if not in a guild)
    /// </summary>
    public string? Guild { get; set; }
}

/// <summary>
/// Request to send a message to a character
/// </summary>
public class SendMessageRequest
{
    /// <summary>
    /// The message to send
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Request to send items to a character
/// </summary>
public class SendItemsRequest
{
    /// <summary>
    /// Item ID
    /// </summary>
    public int ItemId { get; set; }
    
    /// <summary>
    /// Item count
    /// </summary>
    public int Count { get; set; } = 1;
}

/// <summary>
/// Request to send money to a character
/// </summary>
public class SendMoneyRequest
{
    /// <summary>
    /// Amount in copper (1 gold = 10000 copper)
    /// </summary>
    public long CopperAmount { get; set; }
}

/// <summary>
/// Request to kick a player
/// </summary>
public class KickPlayerRequest
{
    /// <summary>
    /// Optional reason for the kick
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request to set character level
/// </summary>
public class SetCharacterLevelRequest
{
    /// <summary>
    /// New level (1-80)
    /// </summary>
    public int Level { get; set; }
}
