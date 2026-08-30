using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace AzerothPlatform.Api.Filters;

/// <summary>
/// Helpers for reading a multipart body as a stream rather than letting the model binder buffer it.
/// Used by the client upload endpoints, where the payload is a whole WoW install.
/// </summary>
public static class MultipartUpload
{
    public static bool IsMultipartContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the boundary from the request content type, rejecting a missing or absurdly long one
    /// (a malformed boundary is otherwise carried into the reader and fails less clearly).
    /// </summary>
    public static string GetBoundary(string? contentType)
    {
        var mediaType = MediaTypeHeaderValue.Parse(contentType);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new InvalidOperationException("The upload is missing a multipart boundary.");
        }

        if (boundary.Length > FormOptions.DefaultMultipartBoundaryLengthLimit)
        {
            throw new InvalidOperationException("The upload's multipart boundary is too long.");
        }

        return boundary;
    }

    /// <summary>True for a section carrying an uploaded file (as opposed to a plain form field).</summary>
    public static bool IsFileSection(ContentDispositionHeaderValue? disposition) =>
        disposition is not null
        && disposition.DispositionType.Equals("form-data")
        && (!string.IsNullOrEmpty(disposition.FileName.Value)
            || !string.IsNullOrEmpty(disposition.FileNameStar.Value));

    /// <summary>The client-supplied file name for a section, unquoted; empty when absent.</summary>
    public static string FileName(ContentDispositionHeaderValue disposition) =>
        HeaderUtilities.RemoveQuotes(
            disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value
        ?? string.Empty;

    /// <summary>
    /// Walks the multipart body and invokes <paramref name="onFile"/> for the first file section,
    /// streaming its body. Returns false when the request contained no file section.
    /// </summary>
    public static async Task<bool> ReadFirstFileAsync(
        Stream body,
        string boundary,
        Func<string, Stream, Task> onFile,
        CancellationToken cancellationToken)
    {
        var reader = new MultipartReader(boundary, body);
        var section = await reader.ReadNextSectionAsync(cancellationToken);

        while (section is not null)
        {
            if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                && IsFileSection(disposition))
            {
                await onFile(FileName(disposition), section.Body);
                return true;
            }

            section = await reader.ReadNextSectionAsync(cancellationToken);
        }

        return false;
    }
}
