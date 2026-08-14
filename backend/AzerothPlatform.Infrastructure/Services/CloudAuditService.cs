using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudAuditService : ICloudAuditService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ICloudAuditActorProvider _actorProvider;
    private readonly ILogger<CloudAuditService> _logger;

    public CloudAuditService(
        AzerothCoreDbContext dbContext,
        ICloudAuditActorProvider actorProvider,
        ILogger<CloudAuditService> logger)
    {
        _dbContext = dbContext;
        _actorProvider = actorProvider;
        _logger = logger;
    }

    public async Task WriteAsync(
        WriteCloudAuditLogRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var eventType = (request.EventType ?? string.Empty).Trim();
        var resourceType = (request.ResourceType ?? string.Empty).Trim();
        var summary = (request.Summary ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(eventType)
            || string.IsNullOrWhiteSpace(resourceType)
            || string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        try
        {
            var entity = new CloudAuditLogEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                OccurredAtUtc = DateTime.UtcNow,
                Actor = _actorProvider.GetActor(),
                EventType = eventType,
                ResourceType = resourceType,
                ResourceId = string.IsNullOrWhiteSpace(request.ResourceId) ? null : request.ResourceId.Trim(),
                Summary = summary.Length > 500 ? summary[..500] : summary,
                MetadataJson = string.IsNullOrWhiteSpace(request.MetadataJson) ? null : request.MetadataJson.Trim(),
            };

            _dbContext.CloudAuditLogs.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write cloud audit log for event {EventType}", eventType);
        }
    }

    public async Task<IReadOnlyList<CloudAuditLogDto>> ListRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        return await _dbContext.CloudAuditLogs
            .AsNoTracking()
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(take)
            .Select(entry => new CloudAuditLogDto
            {
                Id = entry.Id,
                OccurredAtUtc = entry.OccurredAtUtc,
                Actor = entry.Actor,
                EventType = entry.EventType,
                ResourceType = entry.ResourceType,
                ResourceId = entry.ResourceId,
                Summary = entry.Summary,
                MetadataJson = entry.MetadataJson,
            })
            .ToListAsync(cancellationToken);
    }
}
