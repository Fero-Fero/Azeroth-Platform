using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Reads and writes a stack's server configuration files (worldserver.conf, authserver.conf and
/// module .conf files) that are bind-mounted into the containers from
/// <c>{stackId}/azerothcore-wotlk/env/dist/etc</c>.
/// </summary>
public interface IServerConfigService
{
    Task<ServerConfigListDto> ListAsync(string stackId, CancellationToken cancellationToken = default);

    Task<ServerConfigContentDto> ReadAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    Task<ServerConfigListDto> SaveAsync(string stackId, string relativePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies every available <c>*.conf.dist</c> (module checkouts, AzerothCore source, worldserver image)
    /// into the stack etc mirror and materializes a sibling <c>*.conf</c> when missing. Called when a
    /// stack is started so Express / Server Wide Progression can edit configs before worldserver runs.
    /// </summary>
    Task EnsureSeededAsync(string stackId, CancellationToken cancellationToken = default);
}
