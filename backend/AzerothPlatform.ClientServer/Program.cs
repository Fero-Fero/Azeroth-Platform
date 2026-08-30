using AzerothPlatform.ClientServer;
using AzerothPlatform.Core.Contracts;

var builder = WebApplication.CreateBuilder(args);

var options = ClientContentOptions.FromEnvironment(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ClientContentStore>();
builder.Services.AddSingleton<PortalStore>();
builder.Services.AddSingleton<LoginService>();

var app = builder.Build();

// Warm the manifest at startup so the first launcher request is fast. Best-effort: the volumes may
// still be seeding on a fresh stack, in which case the first /manifest request builds it instead.
_ = Task.Run(async () =>
{
    try
    {
        var store = app.Services.GetRequiredService<ClientContentStore>();
        await store.GetManifestAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Initial manifest warm-up failed; it will build on first request.");
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/manifest", async (ClientContentStore store, CancellationToken ct) =>
    Results.Json(await store.GetManifestAsync(ct)));

// A summary of the same manifest, for the manager's Client tab. Separate from /manifest so reading it
// does not transfer the whole file list, and unauthenticated like /manifest because it says nothing
// /manifest does not already say.
app.MapGet("/manifest-status", async (ClientContentStore store, CancellationToken ct) =>
    Results.Json(await store.GetStatusAsync(ct)));

app.MapGet("/files/{**path}", (string path, ClientContentStore store) =>
{
    var absolute = store.ResolveFile(path);
    if (absolute is null)
    {
        return Results.NotFound();
    }

    // Range processing lets the launcher resume partial downloads of multi-GB base files.
    return Results.File(absolute, "application/octet-stream", enableRangeProcessing: true);
});

app.MapPost("/rescan", async (HttpRequest req, ClientContentStore store, CancellationToken ct) =>
{
    if (!IsAuthorized(req, options))
    {
        return Results.Unauthorized();
    }

    var manifest = await store.RescanAsync(ct);
    return Results.Json(new { manifest.Version, manifest.TotalSize, fileCount = manifest.Files.Count });
});

app.MapPost("/force-verify", async (HttpRequest req, ClientContentStore store, CancellationToken ct) =>
{
    if (!IsAuthorized(req, options))
    {
        return Results.Unauthorized();
    }

    var manifest = await store.ForceVerifyAsync(ct);
    return Results.Json(new { manifest.Version, manifest.VerifyToken });
});

app.MapPost("/rebuild-manifest", async (HttpRequest req, ClientContentStore store, CancellationToken ct) =>
{
    if (!IsAuthorized(req, options))
    {
        return Results.Unauthorized();
    }

    var manifest = await store.RebuildManifestAsync(ct);
    var baseFiles = manifest.Files.Where(f => f.Group == ManifestFileGroup.Base).ToList();
    var managedFiles = manifest.Files.Where(f => f.Group == ManifestFileGroup.Managed).ToList();
    return Results.Json(new
    {
        manifest.Version,
        manifest.VerifyToken,
        fileCount = manifest.Files.Count,
        manifest.TotalSize,
        baseFileCount = baseFiles.Count,
        baseTotalSize = baseFiles.Sum(f => f.Size),
        managedFileCount = managedFiles.Count,
        managedTotalSize = managedFiles.Sum(f => f.Size),
    });
});

// ==== Player-facing portal: registry + branding + launcher + login. Served by the stack container so a
// VPC/external stack is self-sufficient (no manager in the player path). ====

app.MapGet("/portal", (PortalStore portal) => Results.Json(portal.GetPortal()));

app.MapPost("/portal", async (HttpRequest req, PortalStore portal, CancellationToken ct) =>
{
    // Only the manager (holding the shared bearer token) may replace the replicated registry snapshot.
    if (!IsAuthorized(req, options))
    {
        return Results.Unauthorized();
    }

    var document = await req.ReadFromJsonAsync<StackPortalDocument>(ct);
    if (document is null)
    {
        return Results.BadRequest(new { error = "Invalid portal document." });
    }

    await portal.SavePortalAsync(document, ct);
    return Results.Ok(new { document.RegistryRevision, stacks = document.Registry.Count });
});

// Player-facing launcher branding (wallpaper/logo) the manager pushed for this stack. Extension-less
// files served with a sniffed content type; 404 when the stack has no branding image.
app.MapGet("/branding/{asset}", (string asset, PortalStore portal) =>
{
    var file = portal.ResolveBrandingFile(asset);
    return file is null
        ? Results.NotFound()
        : Results.File(file.Value.Path, file.Value.ContentType);
});

// Player-facing launcher news the manager pushed for this stack. Served verbatim as the JSON feed the
// launcher fetches; cover images are served extension-less with a sniffed content type.
app.MapGet("/news", (PortalStore portal) =>
    Results.Content(portal.ReadNewsJson() ?? "[]", "application/json"));

app.MapGet("/news-image/{itemId}", (string itemId, PortalStore portal) =>
{
    var file = portal.ResolveNewsImageFile(itemId);
    return file is null
        ? Results.NotFound()
        : Results.File(file.Value.Path, file.Value.ContentType);
});

app.MapGet("/launcher/latest", (PortalStore portal) =>
{
    var artifact = portal.GetLauncherArtifact();
    return artifact.DownloadAvailable
        ? Results.Json(artifact)
        : Results.NotFound(new { error = "No launcher build is available on this stack yet." });
});

app.MapGet("/launcher/download", (PortalStore portal) =>
{
    var file = portal.ResolveLauncherFile();
    if (file is null)
    {
        return Results.NotFound();
    }

    var downloadName = Path.GetFileName(file);
    return Results.File(file, "application/octet-stream", downloadName, enableRangeProcessing: true);
});

app.MapPost("/login", async (LoginRequest? body, LoginService login, CancellationToken ct) =>
{
    if (!login.Enabled)
    {
        // Login not configured for this stack; treat as open so the launcher proceeds.
        return Results.Json(new LoginResponse(true, null));
    }

    if (body is null || string.IsNullOrWhiteSpace(body.Username))
    {
        return Results.Json(new LoginResponse(false, "Invalid username or password."));
    }

    var (success, error) = await login.VerifyAsync(body.Username, body.Password ?? string.Empty, ct);
    return Results.Json(new LoginResponse(success, error));
});

app.Run();

static bool IsAuthorized(HttpRequest request, ClientContentOptions options)
{
    if (string.IsNullOrWhiteSpace(options.AuthToken))
    {
        return true;
    }

    var header = request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        header = header["Bearer ".Length..];
    }

    return string.Equals(header.Trim(), options.AuthToken.Trim(), StringComparison.Ordinal);
}

record LoginRequest(string Username, string Password);
record LoginResponse(bool Success, string? Error);
