using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudAuditService
{
    Task WriteAsync(
        WriteCloudAuditLogRequestDto request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudAuditLogDto>> ListRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);
}
