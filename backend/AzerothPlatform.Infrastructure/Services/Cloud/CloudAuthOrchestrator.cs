using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class CloudAuthOrchestrator : ICloudAuthOrchestrator
{
    private readonly IReadOnlyDictionary<CloudProvider, ICloudProviderAuthStrategy> _strategies;
    private readonly ICloudOAuthStateStore _stateStore;
    private readonly ICloudProviderConnectionService _connectionService;
    private readonly ICloudAuditService _cloudAuditService;

    public CloudAuthOrchestrator(
        IEnumerable<ICloudProviderAuthStrategy> strategies,
        ICloudOAuthStateStore stateStore,
        ICloudProviderConnectionService connectionService,
        ICloudAuditService cloudAuditService)
    {
        _strategies = strategies.ToDictionary(strategy => strategy.Provider);
        _stateStore = stateStore;
        _connectionService = connectionService;
        _cloudAuditService = cloudAuditService;
    }

    public IReadOnlyList<CloudAuthProviderStatusDto> ListProviderStatus()
        => Enum.GetValues<CloudProvider>()
            .Select(GetProviderStatus)
            .ToList();

    public CloudAuthProviderStatusDto GetProviderStatus(CloudProvider provider)
        => Resolve(provider).GetStatus();

    public Task<CloudAuthStartResultDto> StartAsync(
        CloudProvider provider,
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default)
        => Resolve(provider).StartAsync(request ?? new CloudAuthStartRequestDto(), cancellationToken);

    public async Task<CloudProviderConnectionDto> HandleCallbackAsync(
        CloudProvider provider,
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            var detail = string.IsNullOrWhiteSpace(errorDescription) ? error.Trim() : errorDescription.Trim();
            throw new InvalidOperationException($"Cloud sign-in was denied: {detail}");
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("OAuth state is missing.");
        }

        var payload = await _stateStore.TakeAsync(state.Trim(), cancellationToken)
                      ?? throw new InvalidOperationException("OAuth state is invalid or has expired. Start sign-in again.");

        if (payload.Provider != provider)
        {
            throw new InvalidOperationException("OAuth state does not match this cloud provider.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("OAuth authorization code is missing.");
        }

        return await Resolve(provider).HandleCallbackAsync(code.Trim(), payload, cancellationToken);
    }

    public Task<CloudProviderConnectionDto> CompleteAsync(
        CloudProvider provider,
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default)
        => Resolve(provider).CompleteAsync(request ?? new CloudAuthCompleteRequestDto(), cancellationToken);

    public Task RefreshAsync(
        CloudProvider provider,
        string connectionId,
        CancellationToken cancellationToken = default)
        => Resolve(provider).RefreshAsync(connectionId, cancellationToken);

    public async Task RevokeAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connections = await _connectionService.ListAsync(cancellationToken);
        var connection = connections.FirstOrDefault(item =>
                             string.Equals(item.Id, connectionId, StringComparison.OrdinalIgnoreCase))
                         ?? throw new KeyNotFoundException("Cloud connection not found.");

        await Resolve(connection.Provider).RevokeProviderTokenAsync(connection.Id, cancellationToken);

        if (connection.AuthMethod is CloudAuthMethod.OAuth or CloudAuthMethod.AssumedRole)
        {
            await _cloudAuditService.WriteAsync(
                new WriteCloudAuditLogRequestDto
                {
                    EventType = CloudAuditEventTypes.ConnectionOAuthRevoked,
                    ResourceType = "connection",
                    ResourceId = connection.Id,
                    Summary = $"Revoked {connection.Provider} login for \"{connection.Label}\".",
                },
                cancellationToken);
        }

        await _connectionService.DeleteAsync(connection.Id, cancellationToken);
    }

    private ICloudProviderAuthStrategy Resolve(CloudProvider provider)
    {
        if (_strategies.TryGetValue(provider, out var strategy))
        {
            return strategy;
        }

        throw new InvalidOperationException($"No auth strategy is registered for {provider}.");
    }
}