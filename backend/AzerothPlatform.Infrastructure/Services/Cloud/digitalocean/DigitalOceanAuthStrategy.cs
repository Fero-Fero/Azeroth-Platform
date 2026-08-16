using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class DigitalOceanAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;
    private readonly ICloudOAuthStateStore _stateStore;
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly DigitalOceanClient _digitalOceanClient;
    private readonly ISecretProtector _secretProtector;
    private readonly IDigitalOceanTokenResolver _tokenResolver;
    private readonly AzerothCoreDbContext _dbContext;

    public DigitalOceanAuthStrategy(
        IOptions<CloudOAuthOptions> options,
        ICloudOAuthStateStore stateStore,
        ICloudProviderConnectionService connectionService,
        DigitalOceanClient digitalOceanClient,
        ISecretProtector secretProtector,
        IDigitalOceanTokenResolver tokenResolver,
        AzerothCoreDbContext dbContext)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _connectionService = connectionService;
        _digitalOceanClient = digitalOceanClient;
        _secretProtector = secretProtector;
        _tokenResolver = tokenResolver;
        _dbContext = dbContext;
    }

    public override CloudProvider Provider => CloudProvider.DigitalOcean;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.DigitalOcean.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = true,
            SupportsPkce = false,
            SignInLabel = "Sign in with DigitalOcean",
            UnavailableReason = configured
                ? string.Empty
                : "DigitalOcean OAuth is not configured. Set CloudOAuth:DigitalOcean:ClientId and ClientSecret, or use Advanced to paste an API token.",
        };
    }

    public override async Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.DigitalOcean.IsConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        var redirectUri = CloudOAuthRedirectUri.Resolve(
            _options,
            _options.DigitalOcean,
            Provider,
            request.CallbackBaseUrl);

        var state = await _stateStore.CreateAsync(
            Provider,
            codeVerifier: null,
            returnUrl: request.ReturnUrl,
            reconnectConnectionId: request.ReconnectConnectionId,
            label: request.Label,
            cancellationToken);
        state.RedirectUri = redirectUri;

        var authorizationUrl =
            $"{DigitalOceanClient.OAuthAuthorizeUrl}"
            + $"?client_id={Uri.EscapeDataString(_options.DigitalOcean.ClientId.Trim())}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(DigitalOceanClient.OAuthScopes)}"
            + $"&state={Uri.EscapeDataString(state.State)}";

        return new CloudAuthStartResultDto
        {
            AuthorizationUrl = authorizationUrl,
            State = state.State,
        };
    }

    public override async Task<CloudProviderConnectionDto> HandleCallbackAsync(
        string code,
        CloudOAuthStateDto state,
        CancellationToken cancellationToken = default)
    {
        if (!_options.DigitalOcean.IsConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        var redirectUri = string.IsNullOrWhiteSpace(state.RedirectUri)
            ? CloudOAuthRedirectUri.Resolve(_options, _options.DigitalOcean, Provider, requestCallbackBaseUrl: null)
            : state.RedirectUri.Trim();

        var token = await _digitalOceanClient.ExchangeAuthorizationCodeAsync(
            _options.DigitalOcean.ClientId,
            _options.DigitalOcean.ClientSecret,
            redirectUri,
            code,
            cancellationToken);

        var account = await _digitalOceanClient.GetAccountAsync(token.AccessToken, cancellationToken);
        var expiresAt = token.ExpiresIn > 0 ? DateTime.UtcNow.AddSeconds(token.ExpiresIn) : (DateTime?)null;
        var hint = account.DisplayHint;
        if (string.IsNullOrWhiteSpace(hint))
        {
            hint = (token.Info?.Email ?? token.Info?.Name ?? string.Empty).Trim();
        }

        var label = string.IsNullOrWhiteSpace(state.Label)
            ? (string.IsNullOrWhiteSpace(hint) ? "DigitalOcean" : hint)
            : state.Label.Trim();

        return await _connectionService.UpsertOAuthConnectionAsync(
            new UpsertCloudOAuthConnectionRequestDto
            {
                Provider = Provider,
                Label = label,
                AccountHint = hint,
                ProtectedCredentials = CloudProviderCredentialStore.ProtectOAuthTokens(
                    _secretProtector,
                    new CloudProviderCredentialStore.OAuthCredentialEnvelope
                    {
                        AccessToken = token.AccessToken,
                        RefreshToken = token.RefreshToken,
                        ExpiresAtUtc = expiresAt,
                        Scope = token.Scope,
                        Subject = account.Uuid,
                    }),
                TokenExpiresAtUtc = expiresAt,
                ReconnectConnectionId = state.ReconnectConnectionId,
                AuthMethod = CloudAuthMethod.OAuth,
            },
            cancellationToken);
    }

    public override async Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                         .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!string.Equals(entity.Provider, Provider.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This connection is not a DigitalOcean account.");
        }

        var accessToken = await _tokenResolver.ResolveAsync(entity, cancellationToken);
        await _digitalOceanClient.ValidateTokenAsync(accessToken, cancellationToken);
    }

    public override async Task RevokeProviderTokenAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.DigitalOcean.IsConfigured)
        {
            return;
        }

        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
            .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        try
        {
            var token = CloudProviderCredentialStore.UnprotectDigitalOceanToken(
                _secretProtector,
                entity.ProtectedCredentials);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            await _digitalOceanClient.RevokeTokenAsync(
                _options.DigitalOcean.ClientId,
                _options.DigitalOcean.ClientSecret,
                token,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort revoke; connection delete still proceeds.
        }
    }
}
