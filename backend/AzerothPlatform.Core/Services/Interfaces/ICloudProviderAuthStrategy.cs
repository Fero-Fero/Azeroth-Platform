using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudProviderAuthStrategy
{
    CloudProvider Provider { get; }

    CloudAuthProviderStatusDto GetStatus();

    Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CloudProviderConnectionDto> HandleCallbackAsync(
        string code,
        CloudOAuthStateDto state,
        CancellationToken cancellationToken = default);

    Task<CloudProviderConnectionDto> CompleteAsync(
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default);

    Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default);

    Task RevokeProviderTokenAsync(string connectionId, CancellationToken cancellationToken = default);
}

/// <summary>Ephemeral CSRF/PKCE payload stored between <c>/start</c> and <c>/callback</c>.</summary>
public sealed class CloudOAuthStateDto
{
    public string State { get; set; } = string.Empty;

    public CloudProvider Provider { get; set; }

    public string? CodeVerifier { get; set; }

    public string? ReturnUrl { get; set; }

    public string? ReconnectConnectionId { get; set; }

    public string? Label { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
