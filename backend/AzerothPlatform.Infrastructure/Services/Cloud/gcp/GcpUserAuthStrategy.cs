using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud.Auth;

internal sealed class GcpUserAuthStrategy : CloudProviderAuthStrategyBase
{
    private readonly CloudOAuthOptions _options;
    private readonly ICloudOAuthStateStore _stateStore;
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly GcpComputeClient _gcpComputeClient;
    private readonly ISecretProtector _secretProtector;
    private readonly IGcpCredentialResolver _credentialResolver;
    private readonly AzerothCoreDbContext _dbContext;

    public GcpUserAuthStrategy(
        IOptions<CloudOAuthOptions> options,
        ICloudOAuthStateStore stateStore,
        ICloudProviderConnectionService connectionService,
        GcpComputeClient gcpComputeClient,
        ISecretProtector secretProtector,
        IGcpCredentialResolver credentialResolver,
        AzerothCoreDbContext dbContext)
    {
        _options = options.Value;
        _stateStore = stateStore;
        _connectionService = connectionService;
        _gcpComputeClient = gcpComputeClient;
        _secretProtector = secretProtector;
        _credentialResolver = credentialResolver;
        _dbContext = dbContext;
    }

    public override CloudProvider Provider => CloudProvider.Gcp;

    public override CloudAuthProviderStatusDto GetStatus()
    {
        var configured = _options.Gcp.IsConfigured;
        return new CloudAuthProviderStatusDto
        {
            Provider = Provider,
            LoginMode = CloudLoginMode.OAuth,
            IsConfigured = configured,
            IsImplemented = true,
            SupportsPkce = true,
            SignInLabel = "Sign in with Google Cloud",
            UnavailableReason = configured
                ? string.Empty
                : "Paste a Google Cloud service account JSON key below.",
        };
    }

    public override async Task<CloudAuthStartResultDto> StartAsync(
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Gcp.IsConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        var redirectUri = CloudOAuthRedirectUri.Resolve(
            _options,
            _options.Gcp,
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
            $"{GcpComputeClient.OAuthAuthorizeUrl}"
            + $"?client_id={Uri.EscapeDataString(_options.Gcp.ClientId.Trim())}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(GcpComputeClient.OAuthScopes)}"
            + $"&state={Uri.EscapeDataString(state.State)}"
            + "&access_type=offline"
            + "&prompt=consent"
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
        if (!_options.Gcp.IsConfigured)
        {
            throw new InvalidOperationException(GetStatus().UnavailableReason);
        }

        if (string.IsNullOrWhiteSpace(state.CodeVerifier))
        {
            throw new InvalidOperationException("Google Cloud OAuth state is missing the PKCE verifier.");
        }

        var redirectUri = string.IsNullOrWhiteSpace(state.RedirectUri)
            ? CloudOAuthRedirectUri.Resolve(_options, _options.Gcp, Provider, requestCallbackBaseUrl: null)
            : state.RedirectUri.Trim();

        var token = await _gcpComputeClient.ExchangeAuthorizationCodeAsync(
            _options.Gcp.ClientId,
            _options.Gcp.ClientSecret,
            redirectUri,
            code,
            state.CodeVerifier,
            cancellationToken);

        var info = await _gcpComputeClient.GetTokenInfoAsync(token.AccessToken, cancellationToken);
        var expiresAt = token.ExpiresIn > 0
            ? DateTime.UtcNow.AddSeconds(token.ExpiresIn)
            : DateTime.UtcNow.AddHours(1);
        var hint = info.DisplayHint;
        var label = string.IsNullOrWhiteSpace(state.Label)
            ? (string.IsNullOrWhiteSpace(hint) ? "Google Cloud" : hint)
            : state.Label.Trim();

        var access = GcpComputeClient.FromAccessToken(token.AccessToken);
        string? defaultProjectId = null;
        try
        {
            var projects = await _gcpComputeClient.ListProjectsAsync(access, cancellationToken);
            if (projects.Count == 1)
            {
                defaultProjectId = projects[0].Value;
                access = GcpComputeClient.FromAccessToken(token.AccessToken, defaultProjectId);
                await _gcpComputeClient.ValidateAccessAsync(access, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connection still succeeds; the setup dialog will ask for a project and surface Compute errors.
        }

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
                        Scope = token.Scope ?? info.Scope,
                        Subject = info.Subject,
                    }),
                TokenExpiresAtUtc = expiresAt,
                ReconnectConnectionId = state.ReconnectConnectionId,
                DefaultProjectId = defaultProjectId,
                AuthMethod = CloudAuthMethod.OAuth,
            },
            cancellationToken);
    }

    public override async Task<CloudProviderConnectionDto> CompleteAsync(
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var connectionId = (request.ReconnectConnectionId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("A linked Google Cloud connection is required to select a project.");
        }

        var projectId = (request.DefaultProjectId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Select a Google Cloud project.");
        }

        return await _connectionService.SetDefaultProjectAsync(connectionId, projectId, cancellationToken);
    }

    public override async Task RefreshAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                         .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!string.Equals(entity.Provider, Provider.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This connection is not a Google Cloud account.");
        }

        var access = await _credentialResolver.ResolveAsync(entity, cancellationToken);
        if (!string.IsNullOrWhiteSpace(access.ProjectId))
        {
            await _gcpComputeClient.ValidateAccessAsync(access, cancellationToken);
            return;
        }

        _ = await _gcpComputeClient.ListProjectsAsync(access, cancellationToken);
    }

    public override async Task RevokeProviderTokenAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
            .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        try
        {
            if (!CloudProviderCredentialStore.TryUnprotectOAuthTokens(
                    _secretProtector,
                    entity.ProtectedCredentials,
                    out var envelope))
            {
                return;
            }

            var token = string.IsNullOrWhiteSpace(envelope.RefreshToken)
                ? envelope.AccessToken
                : envelope.RefreshToken;
            await _gcpComputeClient.RevokeTokenAsync(token, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort revoke; connection delete still proceeds.
        }
    }
}
