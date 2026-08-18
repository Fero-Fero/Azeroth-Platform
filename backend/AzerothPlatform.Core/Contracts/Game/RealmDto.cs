namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A realm as stored in the AzerothCore <c>acore_auth.realmlist</c> table.
/// </summary>
public class RealmDto
{
    /// <summary>
    /// Realm id (primary key in realmlist). The stack's own realm is id 1.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Realm display name shown in the client's realm-selection screen.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// External address clients are redirected to (managed by the platform on stack start).
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// World server port (managed by the platform on stack start).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Realm type (the realmlist <c>icon</c> column): 0 = Normal/PvE, 1 = PvP, 6 = RP, 8 = RP PvP.
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Realm flags bitmask (the realmlist <c>flag</c> column): 0x02 = Offline, 0x20 = Recommended,
    /// 0x40 = New Players.
    /// </summary>
    public int Flags { get; set; }

    /// <summary>
    /// Realm timezone/region category shown in the client.
    /// </summary>
    public int Timezone { get; set; }

    /// <summary>
    /// Minimum GM security level allowed to connect (0 = everyone, 1 = moderators, 2 = GMs, 3 = admins).
    /// </summary>
    public int AllowedSecurityLevel { get; set; }

    /// <summary>
    /// Current population factor reported by the world server (read-only, live value).
    /// </summary>
    public float Population { get; set; }
}

/// <summary>
/// Request body to create a new realm row. Network details (address/port) are copied from an
/// existing realm so the new row is coherent; the admin can only run it once a world server is
/// attached to it.
/// </summary>
public class CreateRealmRequest
{
    /// <summary>
    /// Realm display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Realm type / <c>icon</c> value (0 Normal, 1 PvP, 6 RP, 8 RP PvP).
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Realm flags bitmask (0x02 Offline, 0x20 Recommended, 0x40 New Players).
    /// </summary>
    public int Flags { get; set; }

    /// <summary>
    /// Realm timezone/region category.
    /// </summary>
    public int Timezone { get; set; }

    /// <summary>
    /// Minimum GM security level allowed to connect (0-3).
    /// </summary>
    public int AllowedSecurityLevel { get; set; }
}

/// <summary>
/// Request body to set the network address clients are redirected to for a stack's realms. The
/// value is persisted as the stack's realmlist host override (so it survives restarts) and applied
/// to the live <c>acore_auth.realmlist</c> rows immediately.
/// </summary>
public class SetRealmAddressRequest
{
    /// <summary>
    /// The host/IP clients should connect to after authenticating (e.g. <c>192.168.1.50</c>). This
    /// is what players' clients are redirected to, so it must be reachable from their machines.
    /// </summary>
    public string Host { get; set; } = string.Empty;
}

/// <summary>
/// Request body to update an existing realm row.
/// </summary>
public class UpdateRealmRequest
{
    /// <summary>
    /// New realm display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Realm type / <c>icon</c> value (0 Normal, 1 PvP, 6 RP, 8 RP PvP).
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Realm flags bitmask (0x02 Offline, 0x20 Recommended, 0x40 New Players).
    /// </summary>
    public int Flags { get; set; }

    /// <summary>
    /// Realm timezone/region category.
    /// </summary>
    public int Timezone { get; set; }

    /// <summary>
    /// Minimum GM security level allowed to connect (0-3).
    /// </summary>
    public int AllowedSecurityLevel { get; set; }
}
