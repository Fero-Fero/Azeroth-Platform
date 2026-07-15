using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manages the realms defined in a stack's <c>acore_auth.realmlist</c> table.
/// </summary>
public interface IRealmService
{
    /// <summary>
    /// Lists all realms defined for the given stack.
    /// </summary>
    Task<List<RealmDto>> GetRealmsAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new realm row in the stack's realmlist. Network details are copied from an existing
    /// realm (or sensible defaults if none exist).
    /// </summary>
    Task<RealmDto> CreateRealmAsync(string stackId, CreateRealmRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the editable properties (name, type, flags, timezone, allowed security level) of a realm.
    /// </summary>
    Task<RealmDto> UpdateRealmAsync(string stackId, int realmId, UpdateRealmRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the network address clients are redirected to for this stack. Persists the value as the
    /// stack's realmlist host override (so it survives stack restarts) and applies it to the live
    /// realmlist rows immediately. Returns the refreshed realm list.
    /// </summary>
    Task<List<RealmDto>> SetRealmAddressAsync(string stackId, string host, CancellationToken cancellationToken = default);
}
