using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudInstanceLifecycleService
{
    /// <summary>
    /// Destroys the cloud VM bound to this stack. Uses stored connection/instance ids when present,
    /// otherwise looks up a running instance by the stack's public host.
    /// </summary>
    Task TerminateStackInstanceAsync(ManagedStackCloudTarget target, CancellationToken cancellationToken = default);
}

public sealed class ManagedStackCloudTarget
{
    public string StackId { get; init; } = string.Empty;

    public string StackName { get; init; } = string.Empty;

    public string PublicHost { get; init; } = string.Empty;

    public string CloudConnectionId { get; init; } = string.Empty;

    public string CloudInstanceId { get; init; } = string.Empty;

    public string CloudRegion { get; init; } = string.Empty;
}
