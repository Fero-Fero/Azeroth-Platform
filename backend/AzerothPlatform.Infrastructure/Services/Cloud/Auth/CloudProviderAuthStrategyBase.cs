using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal abstract class CloudProviderAuthStrategyBase : ICloudProviderAuthStrategy
{
    public abstract CloudProvider Provider { get; }

    public abstract CloudAuthProviderStatusDto GetStatus();

    public virtual Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        if (status.LoginMode is CloudLoginMode.GuidedToken or CloudLoginMode.ManualOnly)
        {
            return Task.FromResult(new CloudAuthStartResultDto
            {
                RequiresManualCredentials = true,
                Message = string.IsNullOrWhiteSpace(status.UnavailableReason)
                    ? "Use Advanced to paste credentials for this provider."
                    : status.UnavailableReason,
            });
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(status.UnavailableReason)
                ? $"{Provider} sign-in is not available yet. Use Advanced to paste credentials."
                : status.UnavailableReason);
    }

    public virtual Task<CloudProviderConnectionDto> HandleCallbackAsync(
        string code,
        CloudOAuthStateDto state,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"{Provider} OAuth callback is not implemented yet.");

    public virtual Task<CloudProviderConnectionDto> CompleteAsync(
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"{Provider} does not use a complete step. Use Sign in or Advanced credentials.");

    public virtual Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException($"{Provider} token refresh is not implemented yet.");

    public virtual Task RevokeProviderTokenAsync(string connectionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}