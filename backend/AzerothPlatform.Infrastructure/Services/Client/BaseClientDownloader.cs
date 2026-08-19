using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Downloads a base client from a configured URL. Supports archive links and public Google Drive
/// folders (file-by-file). Large Drive files may require a confirm-token hop.
/// </summary>
public sealed class BaseClientDownloader
{
    private static readonly Regex ConfirmTokenRegex = new(
        @"confirm=([0-9A-Za-z_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FlipEntryRegex = new(
        @"class=""flip-entry""[^>]*id=""entry-([^""]+)"".*?<a href=""([^""]+)"".*?<div class=""flip-entry-title"">([^<]+)</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HiddenInputRegex = new(
        @"<input[^>]*type=""hidden""[^>]*name=""([^""]+)""[^>]*value=""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FormActionRegex = new(
        @"<form[^>]*id=""download-form""[^>]*action=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<BaseClientDownloader> _logger;

    public BaseClientDownloader(ILogger<BaseClientDownloader> logger)
    {
        _logger = logger;
    }

    public static bool IsGoogleDriveFolder(string url) =>
        IsGoogleDrive(url) && url.Contains("/folders/", StringComparison.OrdinalIgnoreCase);

    public async Task<Stream> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (IsGoogleDriveFolder(url))
        {
            throw new InvalidOperationException(
                "This URL is a Google Drive folder. Use folder download instead of an archive stream.");
        }

        var client = CreateDriveClient();
        try
        {
            return await DownloadFileStreamAsync(client, url.Trim(), ownClient: client, cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task DownloadGoogleDriveFolderAsync(
        string folderUrl,
        Func<string, Stream, CancellationToken, Task> writeFile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderUrl);
        var folderId = TryGetDriveFolderId(folderUrl)
            ?? throw new InvalidOperationException("Could not read a Google Drive folder id from the configured URL.");

        using var client = CreateDriveClient();
        var files = new List<(string RelativePath, string FileId)>();
        await CollectDriveFilesAsync(client, folderId, prefix: "", files, depth: 0, cancellationToken);

        if (files.Count == 0)
        {
            throw new InvalidOperationException("The Google Drive folder listed no downloadable files.");
        }

        _logger.LogInformation("Google Drive folder listed {Count} client files.", files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (relativePath, fileId) = files[i];
            progress?.Report($"Downloading {relativePath} ({i + 1}/{files.Count})…");
            _logger.LogInformation("Downloading Drive file {Index}/{Total}: {Path}", i + 1, files.Count, relativePath);

            var url = $"https://drive.google.com/uc?export=download&id={fileId}&confirm=t";
            await using var stream = await DownloadFileStreamAsync(client, url, ownClient: null, cancellationToken);
            await writeFile(relativePath, stream, cancellationToken);
        }
    }

    private async Task CollectDriveFilesAsync(
        HttpClient client,
        string folderId,
        string prefix,
        List<(string RelativePath, string FileId)> files,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 8)
        {
            throw new InvalidOperationException("Google Drive folder nesting is too deep.");
        }

        var listUrl = $"https://drive.google.com/embeddedfolderview?id={folderId}";
        using var response = await client.GetAsync(listUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        foreach (Match match in FlipEntryRegex.Matches(html))
        {
            var id = match.Groups[1].Value;
            var href = WebUtility.HtmlDecode(match.Groups[2].Value);
            var name = SanitizeEntryName(WebUtility.HtmlDecode(match.Groups[3].Value));
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            {
                continue;
            }

            if (name.Equals("_Readme.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
            if (href.Contains("/folders/", StringComparison.OrdinalIgnoreCase))
            {
                var childId = TryGetDriveFolderId(href) ?? id;
                await CollectDriveFilesAsync(client, childId, relative, files, depth + 1, cancellationToken);
                continue;
            }

            files.Add((NormalizeWowExe(relative), id));
        }
    }

    private async Task<Stream> DownloadFileStreamAsync(
        HttpClient client,
        string url,
        HttpClient? ownClient,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveGoogleDriveUrl(url);
        var response = await client.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (LooksLikeHtml(response) && IsGoogleDrive(resolved))
        {
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            var hop = ResolveDriveConfirmUrl(resolved, html)
                ?? throw new InvalidOperationException(
                    "Google Drive returned an HTML interstitial and no download confirm token was found.");
            _logger.LogInformation("Following Google Drive confirm token for base-client download.");
            response = await client.GetAsync(hop, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (LooksLikeHtml(response))
            {
                response.Dispose();
                throw new InvalidOperationException(
                    "Google Drive did not return the file after the confirm hop. The share may require a sign-in.");
            }
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new HttpResponseStream(response, stream, ownClient);
    }

    private static HttpClient CreateDriveClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromHours(6) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    private static string? ResolveDriveConfirmUrl(string originalUrl, string html)
    {
        var formAction = FormActionRegex.Match(html);
        if (formAction.Success)
        {
            var action = WebUtility.HtmlDecode(formAction.Groups[1].Value).Trim();
            var query = new StringBuilder();
            foreach (Match input in HiddenInputRegex.Matches(html))
            {
                if (query.Length > 0)
                {
                    query.Append('&');
                }

                query.Append(WebUtility.UrlEncode(WebUtility.HtmlDecode(input.Groups[1].Value)));
                query.Append('=');
                query.Append(WebUtility.UrlEncode(WebUtility.HtmlDecode(input.Groups[2].Value)));
            }

            var separator = action.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return $"{action}{separator}{query}";
        }

        var confirm = ConfirmTokenRegex.Match(html);
        return confirm.Success ? AppendQuery(originalUrl, "confirm", confirm.Groups[1].Value) : null;
    }

    private static string? TryGetDriveFolderId(string url)
    {
        var match = Regex.Match(url, @"/folders/([0-9A-Za-z_-]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeEntryName(string name)
    {
        var trimmed = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(c, '_');
        }

        return trimmed.Replace('/', '_').Replace('\\', '_');
    }

    private static string NormalizeWowExe(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Length == 1 && parts[0].Equals("wow.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Wow.exe";
        }

        return relativePath;
    }

    private static bool IsGoogleDrive(string url) =>
        url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHtml(HttpResponseMessage response)
    {
        var media = response.Content.Headers.ContentType?.MediaType;
        return media is not null && media.Contains("html", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveGoogleDriveUrl(string url)
    {
        if (!IsGoogleDrive(url))
        {
            return url;
        }

        var match = Regex.Match(url, @"/file/d/([0-9A-Za-z_-]+)");
        return match.Success
            ? $"https://drive.google.com/uc?export=download&id={match.Groups[1].Value}&confirm=t"
            : url;
    }

    private static string AppendQuery(string url, string key, string value)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{WebUtility.UrlEncode(key)}={WebUtility.UrlEncode(value)}";
    }

    /// <summary>Keeps the HTTP response (and optional client) alive until the consumer finishes reading.</summary>
    private sealed class HttpResponseStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;
        private readonly HttpClient? _client;

        public HttpResponseStream(HttpResponseMessage response, Stream inner, HttpClient? client)
        {
            _response = response;
            _inner = inner;
            _client = client;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
                _client?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
