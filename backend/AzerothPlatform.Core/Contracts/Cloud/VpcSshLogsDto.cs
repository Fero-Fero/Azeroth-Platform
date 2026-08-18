namespace AzerothPlatform.Core.Contracts;

/// <summary>Recent SSH authentication events read from the remote VPC host.</summary>
public class VpcSshLogsDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? LogSource { get; set; }

    public List<VpcSshLogEntryDto> Entries { get; set; } = new();
}

public class VpcSshLogEntryDto
{
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>accepted, failed, invalid-user, closed</summary>
    public string EventType { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? SourceIp { get; set; }

    public string RawLine { get; set; } = string.Empty;
}
