namespace AzerothPlatform.Core.Contracts;

/// <summary>Result of compiling one selected module against the stack's core.</summary>
public class ModuleCheckItemDto
{
    public string ModuleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>pending, compiling, passed, failed, skipped.</summary>
    public string Status { get; set; } = "pending";

    public string? Error { get; set; }

    public string? CommitSha { get; set; }

    public string? Branch { get; set; }

    /// <summary>
    /// Folder under <c>modules/</c> used in compiler paths. Differs from
    /// <see cref="ModuleId"/> when the install hook sets a checkout-folder alias.
    /// </summary>
    public string? CheckoutFolder { get; set; }
}

/// <summary>Last module-compile gate result for a stack.</summary>
public class ModuleCheckStatusDto
{
    public bool Passed { get; set; }

    /// <summary>Operator skipped the compile check and may build Docker images without a matching fingerprint.</summary>
    public bool Skipped { get; set; }

    public string? Fingerprint { get; set; }

    public DateTime? CompletedAt { get; set; }

    public List<ModuleCheckItemDto> Items { get; set; } = new();
}
