using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class AzureEntraAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;
    private readonly ICloudOAuthStateStore _stateStore;
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly AzureComputeClient _azureComputeClient;
    private readonly ISecretProtector _secretProtector;
    private readonly IAzureCredentialResolver _credentialResolver;
    private readonly AzerothCoreDbContext _dbContext;

    public AzureEntraAuthStrategy(
        IOptions<CloudOAuthOptions> options,
        ICloudOAuthStateStore stateStore,
        ICloudProviderConnectionService connectionService,
        AzureComputeClient azureComputeClient,
        ISecretProtector secretProtector,
        IAzureCredentialResolver credentialResolver,
        AzerothCoreDbContext dbContext)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _connectionService = connectionService;
        _azureComputeClient = azureComputeClient;
        _secretProtector = secretProtector;
        _credentialResolver = credentialResolver;
        _dbContext = dbContext;
    }

    public override CloudProvider Provider => CloudProvider.Azure;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Azure.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = true,
            SupportsPkce = true,
            SignInLabel = "Sign in with Microsoft",
            UnavailableReason = configured
                ? string.Empty
                : "Paste an Azure service principal below.",
        };
    }

    public override async Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Azure.IsConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        var tenantId = AzureComputeClient.ResolveTenantId(_options.Azure.TenantId);
        if (request.UseDeviceCode)
        {
            var device = await _azureComputeClient.StartDeviceCodeAsync(
                tenantId,
                _options.Azure.ClientId,
                cancellationToken);
            return new CloudAuthStartResultDto
            {
                DeviceCode = device.DeviceCode,
                UserCode = device.UserCode,
                VerificationUri = string.IsNullOrWhiteSpace(device.VerificationUriComplete)
                    ? device.VerificationUri
                    : device.VerificationUriComplete,
                IntervalSeconds = device.Interval > 0 ? device.Interval : 5,
                Message = "Enter the code at the Microsoft device login page, then wait for this page to finish.",
            };
        }

        var redirectUri = CloudOAuthRedirectUri.Resolve(
            _options,
            _options.Azure,
            Provider,
            request.CallbackBaseUrl);

        var codeVerifier = CloudOAuthPkce.CreateCodeVerifier();
        var state = await _stateStore.CreateAsync(
            Provider,
            codeVerifier,
            returnUrl: request.ReturnUrl,
            reconnectConnectionId: request.ReconnectConnectionId,
            label: request.Label,
            cancellationToken);
        state.RedirectUri = redirectUri;

        var authorizationUrl =
            $"{AzureComputeClient.AuthorizeUrl(tenantId)}"
            + $"?client_id={Uri.EscapeDataString(_options.Azure.ClientId.Trim())}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + "&response_mode=query"
            + $"&scope={Uri.EscapeDataString(AzureComputeClient.OAuthScopes)}"
            + $"&state={Uri.EscapeDataString(state.State)}"
            + $"&code_challenge={Uri.EscapeDataString(CloudOAuthPkce.CreateS256Challenge(codeVerifier))}"
            + "&code_challenge_method=S256";

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
        if (!_options.Azure.IsConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        if (string.IsNullOrWhiteSpace(state.CodeVerifier))
        {
            throw new InvalidOperationException("Azure OAuth state is missing the PKCE verifier.");
        }

        var tenantId = AzureComputeClient.ResolveTenantId(_options.Azure.TenantId);
        var redirectUri = string.IsNullOrWhiteSpace(state.RedirectUri)
            ? CloudOAuthRedirectUri.Resolve(_options, _options.Azure, Provider, requestCallbackBaseUrl: null)
            : state.RedirectUri.Trim();

        var token = await _azureComputeClient.ExchangeAuthorizationCodeAsync(
            tenantId,
            _options.Azure.ClientId,
            _options.Azure.ClientSecret,
            redirectUri,
            code,
            state.CodeVerifier,
            cancellationToken);

        return await PersistOAuthConnectionAsync(
            token,
            tenantId,
            state.Label,
            state.ReconnectConnectionId,
            cancellationToken);
    }

    public override async Task<CloudProviderConnectionDto> CompleteAsync(
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            if (!_options.Azure.IsConfigured)
            {
                throw new InvalidOperationException(GetStatus().UnavailableReason);
            }

            var tenantId = AzureComputeClient.ResolveTenantId(_options.Azure.TenantId);
            var token = await _azureComputeClient.PollDeviceCodeAsync(
                tenantId,
                _options.Azure.ClientId,
                _options.Azure.ClientSecret,
                request.DeviceCode,
                cancellationToken);

            return await PersistOAuthConnectionAsync(
                token,
                tenantId,
                request.Label,
                request.ReconnectConnectionId,
                cancellationToken);
        }

        var connectionId = (request.ReconnectConnectionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A linked Azure connection is required to select a subscription.");
        }

        var subscriptionId = (request.DefaultProjectId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("Select an Azure subscription.");
        }

        return await _connectionService.SetDefaultProjectAsync(connectionId, subscriptionId, cancellationToken);
    }

    public override async Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                         .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!string.Equals(entity.Provider, Provider.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This connection is not an Azure account.");
        }

        var access = await _credentialResolver.ResolveAsync(entity, cancellationToken);
        if (!string.IsNullOrWhiteSpace(access.SubscriptionId))
        {
            await _azureComputeClient.ValidateAccessAsync(access, cancellationToken);
            return;
        }

        _ = await _azureComputeClient.ListSubscriptionsAsync(access, cancellationToken);
    }

    public override Task RevokeProviderTokenAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        _ = connectionId;
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private async Task<CloudProviderConnectionDto> PersistOAuthConnectionAsync(
        AzureComputeClient.AzureOAuthToken token,
        string tenantId,
        string? label,
        string? reconnectConnectionId,
        CancellationToken cancellationToken)
    {
        var expiresAt = token.ExpiresIn > 0
            ? DateTime.UtcNow.AddSeconds(token.ExpiresIn)
            : DateTime.UtcNow.AddHours(1);
        var hint = AzureComputeClient.HintFromIdToken(token.IdToken) ?? string.Empty;
        var resolvedLabel = string.IsNullOrWhiteSpace(label)
            ? (string.IsNullOrWhiteSpace(hint) ? "Azure" : hint)
            : label.Trim();

        string? subscriptionId = null;
        try
        {
            var access = AzureComputeClient.FromAccessToken(
                token.AccessToken,
                new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
                subscriptionId: null,
                tenantId);
            var subscriptions = await _azureComputeClient.ListSubscriptionsAsync(access, cancellationToken);
            if (subscriptions.Count == 1)
            {
                subscriptionId = subscriptions[0].Value;
                access = AzureComputeClient.FromAccessToken(
                    token.AccessToken,
                    new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
                    subscriptionId,
                    tenantId);
                await _azureComputeClient.ValidateAccessAsync(access, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connection still succeeds; the setup dialog asks for a subscription.
        }

        return await _connectionService.UpsertOAuthConnectionAsync(
            new UpsertCloudOAuthConnectionRequestDto
            {
                Provider = Provider,
                Label = resolvedLabel,
                AccountHint = hint,
                ProtectedCredentials = CloudProviderCredentialStore.ProtectOAuthTokens(
                    _secretProtector,
                    new CloudProviderCredentialStore.OAuthCredentialEnvelope
                    {
                        AccessToken = token.AccessToken,
                        RefreshToken = token.RefreshToken,
                        ExpiresAtUtc = expiresAt,
                        Scope = token.Scope,
                        Subject = hint,
                        TenantId = tenantId,
                    }),
                TokenExpiresAtUtc = expiresAt,
                ReconnectConnectionId = reconnectConnectionId,
                DefaultProjectId = subscriptionId,
                AuthMethod = CloudAuthMethod.OAuth,
            },
            cancellationToken);
    }
}
