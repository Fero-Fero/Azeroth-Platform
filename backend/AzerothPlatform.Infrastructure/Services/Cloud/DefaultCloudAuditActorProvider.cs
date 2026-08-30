using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services;

internal sealed class DefaultCloudAuditActorProvider : ICloudAuditActorProvider
{
    public string GetActor() => "admin";
}
