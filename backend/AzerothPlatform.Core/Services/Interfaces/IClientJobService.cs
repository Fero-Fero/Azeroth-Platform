using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs client file-server start/stop/restart/recreate as detached background jobs keyed by stack id.
/// </summary>
public interface IClientJobService
{
    ClientJobStatusDto Enqueue(string stackId, ClientJobAction action);

    ClientJobStatusDto? GetStatus(string stackId);
}
