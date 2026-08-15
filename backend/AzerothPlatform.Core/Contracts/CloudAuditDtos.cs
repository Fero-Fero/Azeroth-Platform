namespace AzerothPlatform.Core.Contracts;

public static class CloudAuditEventTypes
{
    public const string SshKeyCreated = "ssh_key.created";
    public const string SshKeyDeleted = "ssh_key.deleted";
    public const string SshKeyUsed = "ssh_key.used";
    public const string SshKeyDownloaded = "ssh_key.downloaded";
    public const string ConnectionCreated = "connection.created";
    public const string ConnectionDeleted = "connection.deleted";
    public const string ConnectionOAuthLinked = "connection.oauth.linked";
    public const string ConnectionOAuthRefreshed = "connection.oauth.refreshed";
    public const string ConnectionOAuthRevoked = "connection.oauth.revoked";
    public const string ConnectionAssumedRoleLinked = "connection.assumed_role.linked";
    public const string TerminalStarted = "terminal.started";
    public const string TerminalEnded = "terminal.ended";
    public const string LaunchCompleted = "launch.completed";
    public const string CloudFirewallApplied = "cloud_firewall.applied";
}

public sealed class CloudAuditLogDto
{
    public string Id { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string? ResourceId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }
}

public sealed class WriteCloudAuditLogRequestDto
{
    public string EventType { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string? ResourceId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }
}
