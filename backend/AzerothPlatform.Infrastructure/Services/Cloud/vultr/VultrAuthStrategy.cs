using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class VultrAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;
    private readonly ICloudOAuthStateStore _stateStore;
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly VultrClient _vultrClient;
    private readonly ISecretProtector _secretProtector;
    private readonly IVultrTokenResolver _tokenResolver;
    private readonly AzerothCoreDbContext _dbContext;

    public VultrAuthStrategy(
        IOptions<CloudOAuthOptions> options,
        ICloudOAuthStateStore stateStore,
        ICloudProviderConnectionService connectionService,
        VultrClient vultrClient,
        ISecretProtector secretProtector,
        IVultrTokenResolver tokenResolver,
        AzerothCoreDbContext dbContext)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _connectionService = connectionService;
        _vultrClient = vultrClient;
        _secretProtector = secretProtector;
        _tokenResolver = tokenResolver;
        _dbContext = dbContext;
    }

    public override CloudProvider Provider => CloudProvider.Vultr;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Vultr.IsVultrOAuthConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = true,
            SupportsPkce = false,
            SignInLabel = "Sign in with Vultr",
            UnavailableReason = configured
                ? string.Empty
                : "Paste Vultr Client ID, Client secret, and Provider ID below.",
        };
    }

    public override async Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Vultr.IsVultrOAuthConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        var redirectUri = CloudOAuthRedirectUri.Resolve(
            _options,
            _options.Vultr,
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

        var authorizationEndpoint = await _vultrClient.ResolveAuthorizationEndpointAsync(
            _options.Vultr.ProviderId,
            _options.Vultr.AuthorizeUrl,
            cancellationToken);

        var authorizationUrl =
            $"{authorizationEndpoint}"
            + (authorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + $"client_id={Uri.EscapeDataString(_options.Vultr.ClientId.Trim())}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
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
        if (!_options.Vultr.IsVultrOAuthConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        var redirectUri = string.IsNullOrWhiteSpace(state.RedirectUri)
            ? CloudOAuthRedirectUri.Resolve(_options, _options.Vultr, Provider, requestCallbackBaseUrl: null)
            : state.RedirectUri.Trim();

        var token = await _vultrClient.ExchangeAuthorizationCodeAsync(
            _options.Vultr.ProviderId,
            _options.Vultr.ClientId,
            _options.Vultr.ClientSecret,
            redirectUri,
            code,
            cancellationToken);

        var account = await _vultrClient.GetAccountAsync(token.AccessToken, cancellationToken);
        var expiresAt = token.ExpiresIn > 0
            ? DateTime.UtcNow.AddSeconds(token.ExpiresIn)
            : DateTime.UtcNow.AddHours(1);
        var hint = account.DisplayHint;
        var label = string.IsNullOrWhiteSpace(state.Label)
            ? (string.IsNullOrWhiteSpace(hint) ? "Vultr" : hint)
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
                        Subject = account.Email,
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
            throw new InvalidOperationException("This connection is not a Vultr account.");
        }

        var accessToken = await _tokenResolver.ResolveAsync(entity, cancellationToken);
        await _vultrClient.ValidateTokenAsync(accessToken, cancellationToken);
    }

    public override async Task RevokeProviderTokenAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        // Vultr revokes the grant from the customer console. Token delete is best-effort no-op.
        _ = connectionId;
        _ = cancellationToken;
        await Task.CompletedTask;
    }
}
