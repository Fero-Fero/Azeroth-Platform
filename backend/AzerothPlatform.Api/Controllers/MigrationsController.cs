using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Per-stack migration/patch management: enumerate and inspect patches, manage patch files
/// (SQL, DBC CSV, map, MPQ), capture the DBC baseline, and apply patches incrementally.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/migrations")]
public class MigrationsController : ControllerBase
{
    private readonly IMigrationService _migrations;
    private readonly IMigrationApplyRunner _applyRunner;

    public MigrationsController(
        IMigrationService migrations,
        IMigrationApplyRunner applyRunner,
        IIndividualProgressionSyncService individualProgression)
    {
        _migrations = migrations;
        _applyRunner = applyRunner;
        _individualProgression = individualProgression;
    }

    private readonly IIndividualProgressionSyncService _individualProgression;

    /// <summary>Lists all patches with status and per-category file counts.</summary>
    [HttpGet]
    public Task<IActionResult> GetOverview(string stackId, CancellationToken cancellationToken)
        => Execute(() => _migrations.GetOverviewAsync(stackId, cancellationToken));

    /// <summary>Downloads a zip with an empty example patch folder structure and a starter description.md.</summary>
    [HttpGet("patch-template")]
    public async Task<IActionResult> DownloadPatchTemplate(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _migrations.GetPatchTemplateArchiveAsync(stackId, cancellationToken);
            return File(bytes, "application/zip", "patch-template.zip");
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Lists the client MPQ files currently published to this stack's overlay (created by earlier
    /// patches). Used by the patch editor to let an author pick which of them a new patch removes.
    /// </summary>
    [HttpGet("published-mpqs")]
    public Task<IActionResult> GetPublishedMpqs(string stackId, CancellationToken cancellationToken)
        => Execute(() => _migrations.GetPublishedMpqsAsync(stackId, cancellationToken));

    /// <summary>Lists one directory level of the stack's patch folders for the shared file browser.</summary>
    [HttpGet("browse")]
    public Task<IActionResult> BrowsePatchFiles(string stackId, [FromQuery] string? path, CancellationToken cancellationToken)
        => Execute(() => _migrations.BrowsePatchFilesAsync(stackId, path ?? string.Empty, cancellationToken));

    /// <summary>Deletes a file or folder from an unapplied patch. Applied patch content is locked.</summary>
    [HttpDelete("browse/entry")]
    public Task<IActionResult> DeletePatchEntry(string stackId, [FromQuery] string path, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A path is required.");
            }

            await _migrations.DeletePatchEntryAsync(stackId, path, cancellationToken);
            return new { success = true };
        });

    /// <summary>Detailed file listing for a single patch.</summary>
    [HttpGet("{patchKey}")]
    public Task<IActionResult> GetPatch(string stackId, string patchKey, CancellationToken cancellationToken)
        => Execute(() => _migrations.GetPatchAsync(stackId, patchKey, cancellationToken));

    /// <summary>Saves description.md / description.txt for a single patch.</summary>
    [HttpPut("{patchKey}/description")]
    public Task<IActionResult> SavePatchDescription(
        string stackId, string patchKey, [FromBody] SavePatchDescriptionRequest request, CancellationToken cancellationToken)
        => Execute(() => _migrations.SavePatchDescriptionAsync(stackId, patchKey, request?.Content ?? string.Empty, cancellationToken));

    /// <summary>Sets which published MPQ files this patch removes from the client overlay on apply.</summary>
    [HttpPut("{patchKey}/mpq-removals")]
    public Task<IActionResult> SetMpqRemovals(string stackId, string patchKey, [FromBody] SetMpqRemovalsRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            await _migrations.SetMpqRemovalsAsync(stackId, patchKey, request?.FileNames ?? new List<string>(), cancellationToken);
            return new { success = true };
        });

    /// <summary>Creates a new patch folder.</summary>
    [HttpPost]
    public Task<IActionResult> CreatePatch(string stackId, [FromBody] CreatePatchRequest request, CancellationToken cancellationToken)
        => Execute(() => _migrations.CreatePatchAsync(stackId, request, cancellationToken));

    /// <summary>Imports a whole patch collection archive into the stack's migration folders.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8L * 1024 * 1024 * 1024)]
    public Task<IActionResult> ImportCollection(string stackId, [FromForm] IFormFile file, [FromForm] string mode, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (file is null || file.Length == 0)
            {
                throw new ArgumentException("No patch collection archive was uploaded.");
            }

            await using var stream = file.OpenReadStream();
            return await _migrations.ImportPatchCollectionAsync(stackId, stream, mode, cancellationToken);
        });

    /// <summary>Merges SQL and/or client release archives into an existing unapplied patch folder.</summary>
    [HttpPost("import-merge")]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8L * 1024 * 1024 * 1024)]
    public Task<IActionResult> MergeImport(
        string stackId,
        [FromForm] string targetPatchKey,
        [FromForm] IFormFile? sqlArchive,
        [FromForm] IFormFile? clientArchive,
        CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (string.IsNullOrWhiteSpace(targetPatchKey))
            {
                throw new ArgumentException("targetPatchKey is required.");
            }

            Stream? sqlStream = null;
            Stream? clientStream = null;
            try
            {
                if (sqlArchive is { Length: > 0 })
                {
                    sqlStream = sqlArchive.OpenReadStream();
                }

                if (clientArchive is { Length: > 0 })
                {
                    clientStream = clientArchive.OpenReadStream();
                }

                return await _migrations.MergePatchImportAsync(
                    stackId, targetPatchKey, sqlStream, clientStream, cancellationToken);
            }
            finally
            {
                if (sqlStream is not null) await sqlStream.DisposeAsync();
                if (clientStream is not null) await clientStream.DisposeAsync();
            }
        });

    [HttpGet("individual-progression/settings")]
    public Task<IActionResult> GetIndividualProgressionSettings(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.GetSettingsAsync(stackId, cancellationToken));

    [HttpPut("individual-progression/settings")]
    public Task<IActionResult> SaveIndividualProgressionSettings(
        string stackId,
        [FromBody] IndividualProgressionSettingsDto settings,
        CancellationToken cancellationToken)
        => Execute(() => _individualProgression.SaveSettingsAsync(stackId, settings, cancellationToken));

    [HttpPost("individual-progression/discover-keys")]
    public Task<IActionResult> DiscoverIndividualProgressionKeys(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.DiscoverAndMergeSettingsAsync(stackId, cancellationToken: cancellationToken));

    [HttpPost("individual-progression/bootstrap")]
    public Task<IActionResult> BootstrapIndividualProgression(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.BootstrapAsync(stackId, cancellationToken));

    /// <summary>Creates any missing Individual Progression patch template folders without resetting config.</summary>
    [HttpPost("individual-progression/recreate-missing-patches")]
    public Task<IActionResult> RecreateMissingProgressionPatches(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.RecreateMissingPatchesAsync(stackId, cancellationToken));

    /// <summary>
    /// Verifies the stack has all Individual Progression patch templates and that progression config keys
    /// can be read and updated. Required after importing patch content and after each server recompile.
    /// </summary>
    [HttpPost("individual-progression/validate-patches")]
    public Task<IActionResult> ValidateIndividualProgressionPatches(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.ValidatePatchesAsync(stackId, cancellationToken));

    /// <summary>Stub: downloads configured release archives when URLs are populated in appsettings.</summary>
    [HttpPost("individual-progression/download-releases")]
    public Task<IActionResult> DownloadIndividualProgressionReleases(string stackId, CancellationToken cancellationToken)
        => Execute(async () => new { downloaded = 0, skipped = 0, message = "Release URLs are not configured yet." });

    /// <summary>Captures the server_dbc baseline from the running stack's data volume.</summary>
    [HttpPost("init-baseline")]
    public Task<IActionResult> InitializeBaseline(string stackId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            await _migrations.InitializeBaselineAsync(stackId, cancellationToken);
            return new { success = true };
        });

    /// <summary>
    /// Starts applying a patch (must be the next incremental patch above the current level) as a
    /// background run guarded by a cross-user lock. Returns 202 with the initial run status, or 409 if
    /// an apply is already in progress for this stack.
    /// </summary>
    [HttpPost("{patchKey}/apply")]
    public Task<IActionResult> Apply(string stackId, string patchKey, CancellationToken cancellationToken)
        => StartRun(() => _applyRunner.StartApplyAsync(stackId, patchKey, cancellationToken));

    /// <summary>
    /// Starts a background reapply of every already-applied patch — SQL, DBC, maps and MPQ content —
    /// on top of the standard AzerothCore updates (cross-user locked). Returns 202 with the initial run
    /// status, or 409 if an apply is already in progress.
    /// </summary>
    [HttpPost("reapply-sql")]
    public Task<IActionResult> ReapplySql(string stackId, CancellationToken cancellationToken)
        => StartRun(() => _applyRunner.StartReapplyAllAsync(stackId, cancellationToken));

    /// <summary>Live status of the current/last apply run (for polling): phase, log tail, success/error.</summary>
    [HttpGet("apply/status")]
    public IActionResult ApplyStatus(string stackId)
        => new OkObjectResult(_applyRunner.GetStatus(stackId));

    /// <summary>Downloads the full trace log of the latest apply run (or a specific run by id).</summary>
    [HttpGet("apply/log")]
    [HttpGet("apply/log/{runId}")]
    public IActionResult ApplyLog(string stackId, string? runId = null)
    {
        var file = _applyRunner.GetLogFile(stackId, runId);
        if (file is null)
        {
            return new NotFoundObjectResult(new { error = "No trace log is available for this stack." });
        }

        var stream = new FileStream(file.Value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return File(stream, "text/plain", file.Value.FileName);
    }

    [HttpGet("individual-progression/sync/status")]
    public Task<IActionResult> GetProgressionSyncStatus(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.GetSyncStatusAsync(stackId, cancellationToken));

    [HttpPost("individual-progression/sync/run")]
    public Task<IActionResult> RunProgressionSync(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.RunSyncAsync(stackId, cancellationToken));

    [HttpPost("individual-progression/sync/resolve-optional")]
    public Task<IActionResult> ResolveProgressionOptionalFiles(
        string stackId,
        [FromBody] ResolveOptionalFilesRequest request,
        CancellationToken cancellationToken)
        => Execute(() => _individualProgression.ResolveOptionalFilesAsync(stackId, request, cancellationToken));

    [HttpGet("individual-progression/sync/ignored-files")]
    public Task<IActionResult> GetProgressionIgnoredFiles(string stackId, CancellationToken cancellationToken)
        => Execute(() => _individualProgression.GetIgnoredFilesAsync(stackId, cancellationToken));

    [HttpPost("individual-progression/sync/reprompt")]
    public Task<IActionResult> RepromptProgressionIgnoredFile(
        string stackId,
        [FromQuery] string source,
        CancellationToken cancellationToken)
        => Execute(() => _individualProgression.RepromptIgnoredFileAsync(stackId, source, cancellationToken));

    // ===== File operations =====

    /// <summary>
    /// Uploads one or more files into a patch category (dbc, map, mpq, sql/world|auth|characters).
    /// An optional parallel <c>paths</c> form field carries each file's relative path
    /// (e.g. <c>gems/Item.csv</c>) so files can be placed into one-level container sub-folders
    /// (supported by dbc, map and the sql categories).
    /// </summary>
    [HttpPost("{patchKey}/files/{**category}")]
    [RequestSizeLimit(8L * 1024 * 1024 * 1024)]
    // Raise the multipart body-length limit too: RequestSizeLimit alone leaves the default 128 MB
    // per-section cap in place, which 400s large MPQ uploads during form binding.
    [RequestFormLimits(MultipartBodyLengthLimit = 8L * 1024 * 1024 * 1024)]
    public Task<IActionResult> Upload(string stackId, string patchKey, string category, [FromForm] IFormFileCollection files, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (files is null || files.Count == 0)
            {
                throw new ArgumentException("No files were uploaded.");
            }

            var paths = Request.Form["paths"];
            var description = Request.Form["description"].ToString();

            var uploaded = new List<PatchFileDto>();
            var index = 0;
            foreach (var file in files)
            {
                var name = index < paths.Count && !string.IsNullOrWhiteSpace(paths[index])
                    ? paths[index]!
                    : file.FileName;

                await using var stream = file.OpenReadStream();
                uploaded.Add(await _migrations.UploadFileAsync(stackId, patchKey, category!, name, stream, description, cancellationToken));
                index++;
            }

            return uploaded;
        });

    /// <summary>Reads a DBC (.txt/.csv) file for inline editing. Path may include a container sub-folder.</summary>
    [HttpGet("{patchKey}/dbc/{**fileName}")]
    public Task<IActionResult> ReadDbc(string stackId, string patchKey, string fileName, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var content = await _migrations.ReadDbcFileAsync(stackId, patchKey, fileName, cancellationToken);
            return new DbcContentDto { FileName = fileName, Content = content };
        });

    /// <summary>Saves edited content back to a DBC (.txt/.csv) file. Path may include a container sub-folder.</summary>
    [HttpPut("{patchKey}/dbc/{**fileName}")]
    public Task<IActionResult> SaveDbc(string stackId, string patchKey, string fileName, [FromBody] DbcContentDto body, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            await _migrations.SaveDbcFileAsync(stackId, patchKey, fileName, body.Content ?? string.Empty, cancellationToken);
            return new { success = true };
        });

    /// <summary>
    /// Deletes a file. Path is "{category}/{fileName}", e.g. "dbc/Spell.txt", "dbc/gems/Item.csv"
    /// (one container sub-folder for DBC), or "sql/world/foo.sql".
    /// </summary>
    [HttpDelete("{patchKey}/files/{**path}")]
    public Task<IActionResult> DeleteFile(string stackId, string patchKey, string path, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var (category, fileName) = SplitCategoryAndFile(path);
            await _migrations.DeleteFileAsync(stackId, patchKey, category, fileName, cancellationToken);
            return new { success = true };
        });

    // Known categories, longest-prefix first so "sql/world" wins over a bare "sql".
    private static readonly string[] KnownCategories =
        { "sql/world", "sql/auth", "sql/characters", "dbc", "map", "mpq" };

    /// <summary>
    /// Splits a "{category}/{fileName}" path into its category and (possibly sub-foldered) file name.
    /// The file part keeps any DBC container sub-folder (e.g. "gems/Item.csv").
    /// </summary>
    private static (string Category, string FileName) SplitCategoryAndFile(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');

        foreach (var category in KnownCategories)
        {
            if (normalized.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase))
            {
                return (category, normalized[(category.Length + 1)..]);
            }
        }

        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            throw new ArgumentException("Path must be '{category}/{fileName}'.");
        }

        return (normalized[..lastSlash], normalized[(lastSlash + 1)..]);
    }

    // ===== Helpers =====

    /// <summary>
    /// Starts a background apply run: returns 202 (Accepted) with the initial status, 404 if the stack
    /// is missing, or 409 (Conflict) if a run is already in progress. Distinguishes the "already
    /// applying" conflict from other bad-request errors (unlike the generic <see cref="Execute{T}"/>).
    /// </summary>
    private static async Task<IActionResult> StartRun(Func<Task<ApplyStatusDto>> start)
    {
        try
        {
            var status = await start();
            return new AcceptedResult(string.Empty, status);
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return new ConflictObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    private static async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            return new OkObjectResult(result);
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }

    public sealed class DbcContentDto
    {
        public string FileName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}
