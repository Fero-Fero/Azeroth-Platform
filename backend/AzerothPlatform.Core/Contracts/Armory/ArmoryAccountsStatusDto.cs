namespace AzerothPlatform.Core.Contracts;

/// <summary>Runtime status of armory email-verified registration for a stack.</summary>
public class ArmoryAccountsStatusDto
{
    /// <summary>Rows in <c>armory_pending_registration</c> without a linked account.</summary>
    public int PendingRegistrationCount { get; set; }

    /// <summary>False when the auth DB is unreachable or the pending table has not been created yet.</summary>
    public bool PendingTableAvailable { get; set; }
}

public class ArmoryTestEmailRequestDto
{
    public string TestEmailAddress { get; set; } = string.Empty;
}

public class ArmoryTestEmailResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
