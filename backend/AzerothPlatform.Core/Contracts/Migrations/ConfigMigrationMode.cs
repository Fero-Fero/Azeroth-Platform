namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// How the build pipeline reconciles the operator's existing server .conf files with the freshly
/// regenerated configs when a stack is updated or rebuilt. AzerothCore adds/removes config keys
/// between versions, so a plain carry-over would leave new keys missing and removed keys lingering.
/// </summary>
public enum ConfigMigrationMode
{
    /// <summary>
    /// Leave the existing configs untouched (default for plain rebuilds). The persisted etc volume
    /// keeps whatever .conf files it already holds.
    /// </summary>
    Skip,

    /// <summary>
    /// Take the new build's .conf.dist defaults as the base and, for every key present in both, keep
    /// the operator's old value. Keys only in the new config stay at their new defaults; keys only in
    /// the old config are dropped.
    /// </summary>
    Merge,

    /// <summary>
    /// Discard the old .conf files and regenerate fresh from the new .conf.dist defaults. The normal
    /// start flow re-applies managed values (DB credentials, ports, realmlist IP) like initial setup.
    /// </summary>
    Fresh
}
