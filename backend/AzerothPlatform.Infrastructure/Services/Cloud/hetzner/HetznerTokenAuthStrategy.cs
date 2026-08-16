using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class HetznerTokenAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly HetznerCloudClient _hetznerCloudClient;
    private readonly ISecretProtector _secretProtector;
    private readonly AzerothCoreDbContext _dbContext;

    public HetznerTokenAuthStrategy(
        ICloudProviderConnectionService connectionService,
        HetznerCloudClient hetznerCloudClient,
        ISecretProtector secretProtector,
        AzerothCoreDbContext dbContext)
    {
        _connectionService = connectionService;
        _hetznerCloudClient = hetznerCloudClient;
        _secretProtector = secretProtector;
        _dbContext = dbContext;
    }

    public override CloudProvider Provider => CloudProvider.Hetzner;

    public override CloudAuthProviderStatusDto GetStatus()
        => new()
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.GuidedToken,
            IsConfigured = true,
            IsImplemented = true,
            SupportsPkce = false,
            SignInLabel = "Connect Hetzner project",
            UnavailableReason = string.Empty,
        };

    public override Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        _ = request;
        _ = cancellationToken;
        return Task.FromResult(new CloudAuthStartResultDto
        {
            Message = "Create a Read & Write project token in Hetzner Console → Security → API tokens, then paste it here. This is not OAuth.",
        });
    }

    public override async Task<CloudProviderConnectionDto> CompleteAsync(
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var token = (request.AccessToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Paste a Hetzner project API token.");
        }

        await _hetznerCloudClient.ValidateTokenAsync(token, cancellationToken);

        var reconnectId = (request.ReconnectConnectionId ?? string.Empty).Trim();
        var label = string.IsNullOrWhiteSpace(request.Label) ? "Hetzner Cloud" : request.Label.Trim();
        var hint = HetznerCloudClient.MaskToken(token);
        var defaultRegion = string.IsNullOrWhiteSpace(request.DefaultRegion)
            ? null
            : request.DefaultRegion.Trim();

        if (!string.IsNullOrWhiteSpace(reconnectId))
        {
            var entity = await _dbContext.CloudProviderConnections
                             .FirstOrDefaultAsync(connection => connection.Id == reconnectId, cancellationToken)
                         ?? throw new KeyNotFoundException("Cloud connection not found.");
            if (!string.Equals(entity.Provider, Provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Reconnect target is not a Hetzner connection.");
            }

            await _hetznerCloudClient.ProbeWriteAccessAsync(token, cancellationToken);

            entity.ProtectedCredentials = CloudProviderCredentialStore.ProtectApiToken(_secretProtector, token);
            entity.AccountHint = hint;
            entity.NeedsReauth = false;
            entity.TokenExpiresAtUtc = null;
            if (!string.IsNullOrWhiteSpace(request.Label))
            {
                entity.Label = label;
            }

            if (!string.IsNullOrWhiteSpace(defaultRegion))
            {
                entity.DefaultRegion = defaultRegion;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            var verified = await _connectionService.VerifyAsync(entity.Id, cancellationToken);
            return verified.Connection;
        }

        return await _connectionService.CreateAsync(
            new CreateCloudProviderConnectionRequestDto
            {
                Provider = Provider,
                Label = label,
                AccessToken = token,
                DefaultRegion = defaultRegion,
            },
            cancellationToken);
    }

    public override async Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var result = await _connectionService.VerifyAsync(connectionId, cancellationToken);
        if (!result.Ok)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Hetzner token is invalid. Reconnect with a new Read & Write project token."
                    : result.Message);
        }
    }
}
