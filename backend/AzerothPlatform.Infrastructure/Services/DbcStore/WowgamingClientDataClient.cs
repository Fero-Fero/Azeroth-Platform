using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services.DbcStore;

/// <summary>Fetches the latest wowgaming/client-data release and downloads Data.zip.</summary>
public sealed class WowgamingClientDataClient
{
    public const string OwnerRepo = "wowgaming/client-data";
    public const string AssetName = "Data.zip";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WowgamingClientDataClient> _logger;

    public WowgamingClientDataClient(
        IHttpClientFactory httpClientFactory,
        ILogger<WowgamingClientDataClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<WowgamingRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var client = CreateGitHubClient();
        using var response = await client.GetAsync($"repos/{OwnerRepo}/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions)
            ?? throw new InvalidOperationException("GitHub latest release payload was empty.");

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException(
                $"wowgaming/client-data latest release '{release.TagName}' has no {AssetName} asset.");
        }

        return new WowgamingRelease(
            release.TagName ?? "unknown",
            release.PublishedAt,
            asset.BrowserDownloadUrl,
            asset.Size);
    }

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AzerothPlatform", "1.0"));

        onProgress?.Invoke($"Downloading {AssetName}…");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var dst = File.Create(destinationPath);

        var buffer = new byte[1024 * 256];
        long copied = 0;
        var lastLog = DateTime.UtcNow;
        int read;
        while ((read = await src.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(15))
            {
                lastLog = DateTime.UtcNow;
                var msg = total is > 0
                    ? $"Downloading {AssetName}: {copied / (1024 * 1024)} / {total.Value / (1024 * 1024)} MB"
                    : $"Downloading {AssetName}: {copied / (1024 * 1024)} MB";
                onProgress?.Invoke(msg);
                _logger.LogInformation("{Message}", msg);
            }
        }
    }

    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient("GitHubApi");
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri("https://api.github.com/");
        }

        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AzerothPlatform", "1.0"));
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed record WowgamingRelease(string Tag, DateTime? PublishedAt, string DownloadUrl, long SizeBytes);
