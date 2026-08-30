using System.Globalization;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Loads the AzerothCore community module index from the public catalogue.json snapshot and
/// supports importing entries into the platform module catalog.
/// </summary>
public sealed class CommunityModuleCatalogService : ICommunityModuleCatalogService
{
    private const string CatalogueUrl = "https://www.azerothcore.org/data/catalogue.json";
    private const string ModuleTopic = "azerothcore-module";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IModuleCatalogService _moduleCatalogService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CommunityModuleCatalogService> _logger;

    public CommunityModuleCatalogService(
        IHttpClientFactory httpClientFactory,
        IModuleCatalogService moduleCatalogService,
        IMemoryCache cache,
        ILogger<CommunityModuleCatalogService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _moduleCatalogService = moduleCatalogService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CommunityModuleListResult> ListAsync(
        string? search = null,
        string? sort = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var platformModules = await _moduleCatalogService.ListAllAsync(cancellationToken);
        var platformById = platformModules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var platformByRepo = platformModules
            .Where(m => !string.IsNullOrWhiteSpace(m.Repository))
            .GroupBy(m => NormalizeRepository(m.Repository), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var entries = await LoadCommunityModulesAsync(cancellationToken);
        var enriched = entries
            .Select(entry =>
            {
                platformById.TryGetValue(entry.Id, out var byId);
                platformByRepo.TryGetValue(NormalizeRepository(entry.Repository), out var byRepo);
                var platform = byId ?? byRepo;

                entry.InPlatformCatalog = platform is not null;
                entry.IsBuiltIn = platform?.IsBuiltIn ?? false;
                return entry;
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim();
            enriched = enriched
                .Where(entry =>
                    entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Repository.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        enriched = Sort(enriched, sort);

        var total = enriched.Count;
        var items = enriched
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new CommunityModuleListResult
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<ModuleDto> ImportAsync(string repository, CancellationToken cancellationToken = default)
    {
        var normalizedRepo = ModuleCatalogService.ValidateGitRepository(repository);
        var parsed = ParseGitHub(normalizedRepo)
            ?? throw new ArgumentException("Repository must be a GitHub http(s) URL.");

        var (_, repoName) = parsed;
        if (!IsValidModuleId(repoName))
        {
            throw new ArgumentException(
                $"Repository name '{repoName}' is not a valid module id. Module folders must match the repo name and use letters, digits, '.', '_' or '-'.");
        }

        var platformModules = await _moduleCatalogService.ListAllAsync(cancellationToken);
        var existing = platformModules.FirstOrDefault(m =>
                string.Equals(m.Id, repoName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeRepository(m.Repository), NormalizeRepository(normalizedRepo), StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        var communityEntries = await LoadCommunityModulesAsync(cancellationToken);
        var community = communityEntries.FirstOrDefault(entry =>
            string.Equals(NormalizeRepository(entry.Repository), NormalizeRepository(normalizedRepo), StringComparison.OrdinalIgnoreCase));

        if (community is null)
        {
            throw new KeyNotFoundException(
                "This repository was not found in the AzerothCore community module catalogue.");
        }

        return await _moduleCatalogService.CreateAsync(
            new SaveModuleRequest
            {
                Id = community.Id,
                Name = community.Name,
                Description = community.Description,
                Repository = community.Repository,
                Branch = community.Branch,
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<CommunityModuleDto>> LoadCommunityModulesAsync(CancellationToken cancellationToken)
    {
        return await _cache.GetOrCreateAsync(
            "community-module-catalog",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;
                return await FetchCommunityModulesAsync(cancellationToken);
            }) ?? [];
    }

    private async Task<IReadOnlyList<CommunityModuleDto>> FetchCommunityModulesAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);

        using var response = await client.GetAsync(CatalogueUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("organizations", out var organizations)
            || !organizations.TryGetProperty("azerothcore", out var azerothcore)
            || !azerothcore.TryGetProperty(ModuleTopic, out var modulesElement)
            || modulesElement.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Community module catalogue JSON did not contain expected {Topic} array.", ModuleTopic);
            return [];
        }

        var results = new List<CommunityModuleDto>();
        var seenRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in modulesElement.EnumerateArray())
        {
            var repoName = ReadString(item, "name");
            var htmlUrl = ReadString(item, "html_url");
            if (string.IsNullOrWhiteSpace(repoName) || string.IsNullOrWhiteSpace(htmlUrl))
            {
                continue;
            }

            if (!IsValidModuleId(repoName))
            {
                continue;
            }

            var normalizedRepo = NormalizeRepository(htmlUrl);
            if (!seenRepos.Add(normalizedRepo))
            {
                continue;
            }

            var updatedAt = ReadDate(item, "pushed_at") ?? ReadDate(item, "updated_at");
            results.Add(new CommunityModuleDto
            {
                Id = repoName,
                Name = repoName,
                Description = ReadString(item, "description") ?? string.Empty,
                Repository = htmlUrl.Trim().TrimEnd('/'),
                Branch = string.IsNullOrWhiteSpace(ReadString(item, "default_branch")) ? "master" : ReadString(item, "default_branch")!,
                Stars = ReadInt(item, "stargazers_count"),
                Forks = ReadInt(item, "forks_count"),
                UpdatedAt = updatedAt,
            });
        }

        _logger.LogInformation("Loaded {Count} community modules from AzerothCore catalogue.", results.Count);
        return results;
    }

    private static List<CommunityModuleDto> Sort(IReadOnlyList<CommunityModuleDto> items, string? sort)
    {
        return (sort ?? "stars").Trim().ToLowerInvariant() switch
        {
            "name" => items.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "updated" => items
                .OrderByDescending(entry => entry.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(entry => entry.Stars)
                .ToList(),
            _ => items
                .OrderByDescending(entry => entry.Stars)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static bool IsValidModuleId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 64
        && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    private static string NormalizeRepository(string repository)
    {
        var value = repository.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value.ToLowerInvariant();
    }

    private static (string owner, string repo)? ParseGitHub(string repositoryUrl)
    {
        var url = NormalizeRepository(repositoryUrl);
        const string httpsPrefix = "https://github.com/";
        if (!url.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = url[httpsPrefix.Length..];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static DateTimeOffset? ReadDate(JsonElement element, string propertyName)
    {
        var text = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
