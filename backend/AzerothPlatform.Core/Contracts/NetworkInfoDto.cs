namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Host network information used to prefill the realmlist host in the create-stack wizard.
/// </summary>
public class NetworkInfoDto
{
    /// <summary>All detected non-loopback IPv4 addresses on the host.</summary>
    public List<string> Addresses { get; set; } = new();

    /// <summary>The best guess for the LAN address clients should target (may be blank).</summary>
    public string SuggestedRealmlistHost { get; set; } = string.Empty;
}
