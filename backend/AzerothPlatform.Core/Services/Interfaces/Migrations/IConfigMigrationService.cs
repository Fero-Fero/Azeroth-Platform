using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Reconciles a stack's existing server .conf files with the freshly regenerated configs across an
/// update or rebuild. AzerothCore adds/removes config keys between versions, so a plain carry-over
/// would leave new keys missing and removed keys lingering. Capture the old configs before building,
/// then merge them into the new build's defaults (or reset to fresh defaults).
/// </summary>
public interface IConfigMigrationService
{
    /// <summary>
    /// Snapshots the stack's current effective server .conf files (worldserver, authserver, modules)
    /// from its <c>etc</c> volume into a backup location, so a later <see cref="ApplyAsync"/> can merge
    /// the operator's old values into the freshly built configs. Best-effort; safe to call for a stack
    /// that has never been started (nothing to capture).
    /// </summary>
    Task CaptureAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the chosen <see cref="ConfigMigrationMode"/> after a build produces new images: extracts
    /// the new <c>*.conf.dist</c> defaults from the freshly built worldserver/authserver images and
    /// writes effective <c>*.conf</c> into the stack's local etc mirror (which is seeded into the
    /// <c>etc</c> volume on the next start). Merge preserves old values per key; Fresh keeps new
    /// defaults; Skip does nothing. Best-effort; a failure leaves the existing configs untouched.
    /// </summary>
    Task ApplyAsync(string stackId, ConfigMigrationMode mode, CancellationToken cancellationToken = default);
}
