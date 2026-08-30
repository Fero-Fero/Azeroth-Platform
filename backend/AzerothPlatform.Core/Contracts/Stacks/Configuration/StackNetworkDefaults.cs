namespace AzerothPlatform.Core.Contracts;

/// <summary>Default host ports for stack web services (armory, client file server).</summary>
public static class StackNetworkDefaults
{
    /// <summary>Default host port for the armory website (first stack on a host).</summary>
    public const int DefaultArmoryPort = 8100;

    /// <summary>Default host port for the client patch / launcher file server.</summary>
    public const int DefaultClientPort = 8101;

    /// <summary>Start of the dynamic port scan range when defaults are taken.</summary>
    public const int PortRangeStart = DefaultArmoryPort;

    /// <summary>Exclusive end of the dynamic port scan range.</summary>
    public const int PortRangeEnd = 10100;
}
