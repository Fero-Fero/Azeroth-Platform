namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Per-stack network settings for the player-facing HTTP services (armory + client file server): which
/// host ports they publish on and which host interface those ports bind to. Lets an operator expose the
/// armory beyond localhost (LAN / VPC / all interfaces) from the UI, without editing the generated
/// <c>.env</c> — which is unreachable once the stack runs on a remote host.
/// </summary>
public class ArmoryNetworkConfigDto
{
    /// <summary>Host port the armory (frontend-armory) is published on.</summary>
    public int ArmoryPort { get; set; }

    /// <summary>Host port the client file server (launcher downloads) is published on.</summary>
    public int ClientPort { get; set; }

    /// <summary>
    /// Host interface override the armory + client ports bind to. Blank inherits the manager default;
    /// <c>0.0.0.0</c> = all interfaces; or a specific IP. On input, anything other than blank/an IP/
    /// <c>0.0.0.0</c> is rejected.
    /// </summary>
    public string BindAddress { get; set; } = string.Empty;

    /// <summary>
    /// Read-only: the interface actually used once the override + deployment policy are applied (e.g.
    /// <c>127.0.0.1</c>, <c>0.0.0.0</c>, or a specific IP). Populated by the server for display.
    /// </summary>
    public string EffectiveBindAddress { get; set; } = string.Empty;

    /// <summary>Read-only: whether this is a local stack (bind defaults to loopback) vs external.</summary>
    public bool IsLocalDeployment { get; set; }

    /// <summary>Read-only: whether the armory container is currently running (affects apply timing).</summary>
    public bool ArmoryRunning { get; set; }
}
