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
}
