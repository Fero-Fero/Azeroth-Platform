using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Talks to a single stack's own portal/client container: the self-describing <c>/portal</c> document
/// (registry + branding + launcher artifact), <c>/health</c>, <c>/login</c> and <c>/launcher/*</c>.
/// Manifest + file downloads use <see cref="ManifestClient.ForContent"/> against the same base URL. This
/// is the launcher's only player-facing channel; the manager is never contacted at play time.
/// </summary>
public sealed class PortalClient : ILauncherArtifactSource
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public string BaseUrl { get; }

    public PortalClient(string portalUrl, TimeSpan? timeout = null)
    {
        BaseUrl = portalUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = timeout ?? DefaultTimeout
        };
    }

    /// <summary>Fetches this stack's portal document (registry + branding + launcher info).</summary>
    public async Task<StackPortalDocument?> GetPortalAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<StackPortalDocument>("portal", JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Cheap reachability probe (GET /health). True when the stack answers 2xx quickly.</summary>
    public async Task<bool> PingHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync("health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Verifies account credentials against this stack's own auth DB (POST /login).</summary>
    public async Task<LauncherLoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "login", new { username, password }, JsonOptions, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<LauncherLoginResponse>(JsonOptions, cancellationToken);
            return result ?? new LauncherLoginResponse { Success = false, Error = "The login server returned an unexpected response." };
        }
        catch
        {
            return new LauncherLoginResponse { Success = false, Error = "Could not reach the login server. Please try again." };
        }
    }

    /// <summary>The launcher build this stack serves (for self-update), or null when none/unreachable.</summary>
    public async Task<LauncherArtifactInfo?> GetLauncherLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<LauncherArtifactInfo>("launcher/latest", JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Maps this stack's launcher artifact onto the shared artifact-source shape (self-update).</summary>
    public async Task<(string? Version, string? Sha256, bool Available)> GetLatestAsync(CancellationToken cancellationToken)
    {
        var artifact = await GetLauncherLatestAsync(cancellationToken);
        return (artifact?.Version, artifact?.Sha256, artifact?.DownloadAvailable ?? false);
    }

    /// <summary>Downloads this stack's launcher exe (artifact-source contract).</summary>
    public Task DownloadAsync(string destinationPath, CancellationToken cancellationToken)
        => DownloadLauncherAsync(destinationPath, cancellationToken);

    /// <summary>Downloads this stack's launcher exe to a local path (GET /launcher/download).</summary>
    public async Task DownloadLauncherAsync(string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            "launcher/download", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }
}
