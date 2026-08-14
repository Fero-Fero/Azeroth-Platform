using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface IClientEventPublisher
{
    Task PublishStatusAsync(ClientJobStatusDto status);
}
