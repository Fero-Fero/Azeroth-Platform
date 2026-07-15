using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Fetches the merged client manifest and downloads game files directly from a stack's own
/// client-server container (<c>/manifest</c> and <c>/files/{path}</c>). The manager is never involved.
/// </summary>
public sealed class ManifestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private readonly string _basePath;

    private ManifestClient(Uri baseAddress, string basePath)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(30)
        };

        _basePath = basePath;
    }

    /// <summary>
    /// Creates a client targeting a stack's self-contained client-server container, which serves the
    /// merged manifest at <c>/manifest</c> and files at <c>/files/{path}</c> (no <c>api/</c> prefix).
    /// </summary>
    public static ManifestClient ForContent(string contentBaseUrl) =>
        new(new Uri(contentBaseUrl.TrimEnd('/') + "/"), string.Empty);

    public async Task<ClientManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        var manifest = await _http.GetFromJsonAsync<ClientManifest>(
            _basePath + "manifest", JsonOptions, cancellationToken);
        return manifest ?? throw new InvalidOperationException("Server returned an empty manifest.");
    }

    /// <summary>
    /// Downloads a single file to <paramref name="destinationPath"/>, resuming from any existing
    /// ".part" temp file via HTTP range requests. Reports bytes written for this file.
    /// </summary>
    public async Task DownloadFileAsync(
        string relativePath,
        string destinationPath,
        long expectedSize,
        IProgress<long>? fileProgress,
        CancellationToken cancellationToken)
    {
        var partPath = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        long existing = 0;
        if (File.Exists(partPath))
        {
            existing = new FileInfo(partPath).Length;
            if (existing > expectedSize)
            {
                File.Delete(partPath);
                existing = 0;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _basePath + "files/" + EncodePath(relativePath));
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // If the server ignored the range (200 instead of 206), restart from scratch.
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append && existing > 0)
        {
            existing = 0;
        }

        response.EnsureSuccessStatusCode();

        if (fileProgress is not null && existing > 0)
        {
            fileProgress.Report(existing);
        }

        var fileMode = append ? FileMode.Append : FileMode.Create;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(
            partPath, fileMode, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024, useAsync: true))
        {
            var buffer = new byte[1024 * 1024];
            var total = existing;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                fileProgress?.Report(total);
            }
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(partPath, destinationPath);
    }

    private static string EncodePath(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }
}
