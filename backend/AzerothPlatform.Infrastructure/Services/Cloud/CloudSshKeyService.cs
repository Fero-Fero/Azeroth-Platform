using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudSshKeyService : ICloudSshKeyService
{
    public const string ManagerOnlyLabelPrefix = "azp-bootstrap:";

    private static readonly Regex PemBodyPattern = new(
        "-----BEGIN [^-]+-----\\s*(?<body>[A-Za-z0-9+/=\\s]+)\\s*-----END [^-]+-----",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly ICloudAuditService _cloudAuditService;

    public CloudSshKeyService(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        ICloudAuditService cloudAuditService)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _cloudAuditService = cloudAuditService;
    }

    public async Task<IReadOnlyList<CloudSshKeyDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.CloudSshKeys
            .AsNoTracking()
            .Where(key =>
                !key.Label.StartsWith(ManagerOnlyLabelPrefix)
                && !key.Label.StartsWith("Bootstrap "))
            .OrderByDescending(key => key.CreatedAtUtc)
            .Select(key => ToDto(key))
            .ToListAsync(cancellationToken);
    }

    public async Task<CloudSshKeyDto> CreateAsync(
        CreateCloudSshKeyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var pem = NormalizePrivateKey(request.PrivateKey);
        ValidatePrivateKey(pem);

        var label = (request.Label ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = $"SSH key {ComputeFingerprint(pem)}";
        }

        if (label.Length > 100)
        {
            throw new ArgumentException("Label must be 100 characters or fewer.");
        }

        var entity = new CloudSshKeyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = label,
            ProtectedPrivateKey = _secretProtector.Protect(pem),
            Fingerprint = ComputeFingerprint(pem),
            DefaultSshUser = (request.DefaultSshUser ?? string.Empty).Trim(),
            CreatedAtUtc = DateTime.UtcNow,
        };

        _dbContext.CloudSshKeys.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.SshKeyCreated,
                ResourceType = "ssh_key",
                ResourceId = entity.Id,
                Summary = $"Saved SSH key \"{entity.Label}\" (fingerprint {entity.Fingerprint}).",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    label = entity.Label,
                    fingerprint = entity.Fingerprint,
                    defaultSshUser = entity.DefaultSshUser,
                }),
            },
            cancellationToken);

        return ToDto(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudSshKeys.FirstOrDefaultAsync(key => key.Id == id, cancellationToken)
                     ?? throw new KeyNotFoundException("SSH key not found.");

        _dbContext.CloudSshKeys.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteDeletedAuditAsync(entity, cancellationToken);
    }

    public async Task DeleteUnusedKeysForStackAsync(string stackId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stackId))
        {
            return;
        }

        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return;
        }

        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var candidateFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSavedSshKeyIds(stack.WizardDraftJson, candidateIds);
        TryAddFingerprint(stack.ExternalSshPrivateKey, candidateFingerprints);

        if (candidateIds.Count == 0 && candidateFingerprints.Count == 0)
        {
            return;
        }

        var others = await _dbContext.ManagedStacks.AsNoTracking()
            .Where(item => item.Id != stackId)
            .Select(item => new { item.ExternalSshPrivateKey, item.WizardDraftJson })
            .ToListAsync(cancellationToken);

        var reservedIds = new HashSet<string>(StringComparer.Ordinal);
        var reservedFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in others)
        {
            CollectSavedSshKeyIds(other.WizardDraftJson, reservedIds);
            TryAddFingerprint(other.ExternalSshPrivateKey, reservedFingerprints);
        }

        var keys = await _dbContext.CloudSshKeys.ToListAsync(cancellationToken);
        var toDelete = keys
            .Where(key =>
                (candidateIds.Contains(key.Id) || candidateFingerprints.Contains(key.Fingerprint))
                && !reservedIds.Contains(key.Id)
                && !reservedFingerprints.Contains(key.Fingerprint))
            .ToList();

        if (toDelete.Count == 0)
        {
            return;
        }

        _dbContext.CloudSshKeys.RemoveRange(toDelete);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var entity in toDelete)
        {
            await WriteDeletedAuditAsync(entity, cancellationToken);
        }
    }

    public async Task<string> ResolvePrivateKeyAsync(
        string id,
        string? usageContext = null,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudSshKeys.AsNoTracking()
                         .FirstOrDefaultAsync(key => key.Id == id, cancellationToken)
                     ?? throw new KeyNotFoundException("SSH key not found.");

        var pem = _secretProtector.Unprotect(entity.ProtectedPrivateKey);
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException(
                "The saved SSH key could not be decrypted. Re-import the key (often after the manager encryption key was reset).");
        }

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.SshKeyUsed,
                ResourceType = "ssh_key",
                ResourceId = entity.Id,
                Summary = $"Used saved SSH key \"{entity.Label}\" (fingerprint {entity.Fingerprint}).",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    label = entity.Label,
                    fingerprint = entity.Fingerprint,
                    usageContext = string.IsNullOrWhiteSpace(usageContext) ? "resolved" : usageContext.Trim(),
                }),
            },
            cancellationToken);

        return pem;
    }

    public async Task<CloudSshKeyExportDto> ExportAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudSshKeys.AsNoTracking()
                         .FirstOrDefaultAsync(key => key.Id == id, cancellationToken)
                     ?? throw new KeyNotFoundException("SSH key not found.");

        var pem = _secretProtector.Unprotect(entity.ProtectedPrivateKey);
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException(
                "The saved SSH key could not be decrypted. Re-import the key (often after the manager encryption key was reset).");
        }

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.SshKeyDownloaded,
                ResourceType = "ssh_key",
                ResourceId = entity.Id,
                Summary = $"Downloaded SSH key \"{entity.Label}\" (fingerprint {entity.Fingerprint}).",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    label = entity.Label,
                    fingerprint = entity.Fingerprint,
                }),
            },
            cancellationToken);

        return new CloudSshKeyExportDto
        {
            Id = entity.Id,
            Label = entity.Label,
            Fingerprint = entity.Fingerprint,
            DefaultSshUser = entity.DefaultSshUser,
            PrivateKey = pem,
        };
    }

    internal static string ComputeFingerprint(string pem)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pem));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    internal static bool IsManagerOnlyLabel(string? label)
    {
        var value = (label ?? string.Empty).Trim();
        return value.StartsWith(ManagerOnlyLabelPrefix, StringComparison.Ordinal)
               || value.StartsWith("Bootstrap ", StringComparison.Ordinal);
    }

    internal static string NormalizePrivateKey(string pem)
        => (pem ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    internal static void ValidatePrivateKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new ArgumentException("SSH private key is required.");
        }

        if (!pem.Contains("BEGIN", StringComparison.Ordinal) || !pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new ArgumentException("SSH private key must be PEM-encoded.");
        }

        if (!PemBodyPattern.IsMatch(pem))
        {
            throw new ArgumentException("SSH private key format is invalid.");
        }
    }

    private static CloudSshKeyDto ToDto(CloudSshKeyEntity entity)
        => new()
        {
            Id = entity.Id,
            Label = entity.Label,
            Fingerprint = entity.Fingerprint,
            DefaultSshUser = entity.DefaultSshUser,
            CreatedAtUtc = entity.CreatedAtUtc,
        };

    private async Task WriteDeletedAuditAsync(CloudSshKeyEntity entity, CancellationToken cancellationToken)
    {
        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.SshKeyDeleted,
                ResourceType = "ssh_key",
                ResourceId = entity.Id,
                Summary = $"Deleted SSH key \"{entity.Label}\" (fingerprint {entity.Fingerprint}).",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    label = entity.Label,
                    fingerprint = entity.Fingerprint,
                }),
            },
            cancellationToken);
    }

    private void TryAddFingerprint(string? protectedPem, ISet<string> fingerprints)
    {
        if (string.IsNullOrWhiteSpace(protectedPem))
        {
            return;
        }

        try
        {
            var pem = NormalizePrivateKey(_secretProtector.Unprotect(protectedPem));
            if (string.IsNullOrWhiteSpace(pem))
            {
                return;
            }

            fingerprints.Add(ComputeFingerprint(pem));
        }
        catch (Exception)
        {
            // Skip keys that cannot be decrypted; stack delete should still proceed.
        }
    }

    private static void CollectSavedSshKeyIds(string? json, ISet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            CollectSavedSshKeyIds(document.RootElement, ids);
        }
        catch (JsonException)
        {
            // Draft JSON is best-effort; a corrupt snapshot must not block stack delete.
        }
    }

    private static void CollectSavedSshKeyIds(JsonElement element, ISet<string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.NameEquals("savedSshKeyId") || property.NameEquals("bootstrapSshKeyId"))
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var id = (property.Value.GetString() ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            ids.Add(id);
                        }
                    }
                    else
                    {
                        CollectSavedSshKeyIds(property.Value, ids);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectSavedSshKeyIds(item, ids);
                }

                break;
        }
    }
}
