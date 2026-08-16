using System.IO.Compression;
using System.Text;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Defines and creates the on-disk layout for a stack's migration/patch system:
///   {stackRoot}/migrations/{patch}/{sql/{world,auth,characters},dbc,map,mpq}
///   {stackRoot}/server_dbc          (cumulative DBC baseline)
///   {stackRoot}/client/game/Data    (per-stack public launcher content)
/// </summary>
public static class MigrationLayout
{
    public const string MigrationsDirName = "migrations";
    public const string ServerDbcDirName = "server_dbc";
    public const string ClientDirName = "client";
    public const string LuaScriptsDirName = "lua_scripts";
    public const string RevisionsDirName = "revisions";

    /// <summary>Expansion roots used in patch indices: classic=1, tbc=2, wotlk=3, custom=4.</summary>
    public static readonly IReadOnlyDictionary<string, int> ExpansionRoots =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["classic"] = 1,
            ["tbc"] = 2,
            ["wotlk"] = 3,
            ["custom"] = 4
        };

    public static int ExpansionRoot(string expansion) =>
        ExpansionRoots.TryGetValue(expansion.Trim(), out var root)
            ? root
            : throw new ArgumentException("Expansion must be one of: classic, tbc, wotlk, custom.");

    public static string ExpansionName(int root) => root switch
    {
        1 => "classic",
        2 => "tbc",
        3 => "wotlk",
        4 => "custom",
        _ => throw new ArgumentException($"Unknown expansion root: {root}.")
    };

    /// <summary>Default patch folders created for every new stack (expansion entry points).</summary>
    public static readonly IReadOnlyList<string> DefaultPatches = new[]
    {
        "patch 1.0",
        "patch 2.0",
        "patch 3.0",
        "patch 4.0"
    };

    /// <summary>Built-in placeholder folders created for every new stack.</summary>
    public static IEnumerable<string> AllPlaceholderPatches => DefaultPatches;

    /// <summary>Placeholder descriptions seeded for the default expansion patches.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultPatchDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["patch 1.0"] = "Initial placeholder for Classic expansion.",
            ["patch 2.0"] = "Initial placeholder for The Burning Crusade expansion.",
            ["patch 3.0"] = "Initial placeholder for Wrath of the Lich King expansion.",
            ["patch 4.0"] = "Initial placeholder for custom content.",
        };

    public static readonly IReadOnlyList<string> PatchDescriptionFileNames = new[] { "description.md", "description.txt" };

    /// <summary>Target databases for SQL sub-folders, mapped to the AzerothCore schema names.</summary>
    public static readonly IReadOnlyDictionary<string, string> SqlDatabases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["world"] = "acore_world",
            ["auth"] = "acore_auth",
            ["characters"] = "acore_characters"
        };

    public static string MigrationsRoot(string stackRoot) => Path.Combine(stackRoot, MigrationsDirName);

    public static string PatchDir(string stackRoot, string patchKey) =>
        Path.Combine(MigrationsRoot(stackRoot), patchKey);

    public static string SqlDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "sql");

    public static string SqlDatabaseDir(string stackRoot, string patchKey, string database) =>
        Path.Combine(SqlDir(stackRoot, patchKey), database);

    public static string DbcDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "dbc");

    public static string MapDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "map");

    public static string MpqDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "mpq");

    public static string ConfigDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "config");

    /// <summary>Lua scripts staged in a patch before apply copies them to <see cref="LuaScriptsDir"/>.</summary>
    public static string PatchLuaDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "lua");

    /// <summary>
    /// Per-stack checkout of Azeroth-Platform-Progression (cloned/updated by progression sync).
    /// </summary>
    public static string ProgressionRepoDir(string stackRoot) =>
        Path.Combine(stackRoot, ProgressionRepoDirName);

    public const string ProgressionRepoDirName = "azeroth-platform-progression";

    public static string ServerDbcDir(string stackRoot) => Path.Combine(stackRoot, ServerDbcDirName);

    public static string ClientGameDir(string stackRoot) =>
        Path.Combine(stackRoot, ClientDirName, "game");

    public static string ClientDataDir(string stackRoot) =>
        Path.Combine(ClientGameDir(stackRoot), "Data");

    /// <summary>
    /// Per-stack client <b>overlay</b> root (read-write layer the client-server container mounts at
    /// <c>/client/overlay</c>). Published patch MPQs / addons live here, layered over the shared
    /// read-only base client. Mirrors <c>StackService.ClientOverlayDir</c>.
    /// </summary>
    public static string ClientOverlayDir(string stackRoot) =>
        Path.Combine(stackRoot, ClientDirName, "overlay");

    /// <summary>Overlay <c>Data/</c> dir where published patch MPQs land (served as Managed content).</summary>
    public static string ClientOverlayDataDir(string stackRoot) =>
        Path.Combine(ClientOverlayDir(stackRoot), "Data");

    /// <summary>Per-stack client settings templates dir (realmlist.wtf.tmpl etc.) served to the launcher.</summary>
    public static string ClientSettingsDir(string stackRoot) =>
        Path.Combine(stackRoot, ClientDirName, "settings");

    /// <summary>Directory holding Lua scripts bind-mounted into the worldserver (Eluna ScriptPath).</summary>
    public static string LuaScriptsDir(string stackRoot) => Path.Combine(stackRoot, LuaScriptsDirName);

    /// <summary>Directory holding a stack's launcher-profile branding assets (background/logo/news).</summary>
    public static string LauncherProfileDir(string stackRoot) =>
        Path.Combine(stackRoot, ClientDirName, "launcher-profile");

    /// <summary>Root directory holding all point-in-time revisions (snapshots) for a stack.</summary>
    public static string RevisionsDir(string stackRoot) => Path.Combine(stackRoot, RevisionsDirName);

    /// <summary>Directory holding a single revision's dump files, config copy, and metadata.</summary>
    public static string RevisionDir(string stackRoot, string revisionId) =>
        Path.Combine(RevisionsDir(stackRoot), revisionId);

    /// <summary>The stack's live server config directory (env/dist/etc), snapshotted per revision.</summary>
    public static string EtcDir(string stackRoot) =>
        Path.Combine(stackRoot, "azerothcore-wotlk", "env", "dist", "etc");

    /// <summary>Creates the default migration directory scaffold for a stack (idempotent).</summary>
    /// <param name="stackRoot">The stack's build directory.</param>
    /// <param name="settingsTemplateSource">
    /// Optional source directory of client settings templates (realmlist.wtf.tmpl etc.) to seed the
    /// stack's <c>client/settings/</c> so the launcher always receives a realmlist.wtf.
    /// </param>
    public static void EnsureScaffold(string stackRoot, string? settingsTemplateSource = null)
    {
        Directory.CreateDirectory(ServerDbcDir(stackRoot));
        Directory.CreateDirectory(ClientDataDir(stackRoot));
        Directory.CreateDirectory(ClientSettingsDir(stackRoot));
        Directory.CreateDirectory(LuaScriptsDir(stackRoot));

        SeedClientSettings(stackRoot, settingsTemplateSource);

        foreach (var patch in DefaultPatches)
        {
            EnsurePatchDirectories(stackRoot, patch);
        }
    }

    /// <summary>
    /// Copies client settings templates into the stack's <c>client/settings/</c> when they are missing,
    /// never overwriting an operator's existing customizations (idempotent, additive).
    /// </summary>
    public static void SeedClientSettings(string stackRoot, string? settingsTemplateSource)
    {
        if (string.IsNullOrWhiteSpace(settingsTemplateSource) || !Directory.Exists(settingsTemplateSource))
        {
            return;
        }

        var destDir = ClientSettingsDir(stackRoot);
        Directory.CreateDirectory(destDir);

        foreach (var source in Directory.EnumerateFiles(settingsTemplateSource, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(settingsTemplateSource, source);
            var destination = Path.Combine(destDir, relative);
            var destParent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            if (!File.Exists(destination))
            {
                File.Copy(source, destination);
            }
        }
    }

    /// <summary>Creates default patch folders (patch 1–4) when missing (idempotent).</summary>
    public static void EnsureDefaultPatches(string stackRoot)
    {
        Directory.CreateDirectory(MigrationsRoot(stackRoot));
        foreach (var patch in DefaultPatches)
        {
            EnsurePatchDirectories(stackRoot, patch);
        }
    }

    /// <summary>Creates the sub-folders for a single patch (idempotent).</summary>
    public static void EnsurePatchDirectories(string stackRoot, string patchKey)
    {
        foreach (var database in SqlDatabases.Keys)
        {
            Directory.CreateDirectory(SqlDatabaseDir(stackRoot, patchKey, database));
        }

        Directory.CreateDirectory(DbcDir(stackRoot, patchKey));
        Directory.CreateDirectory(MapDir(stackRoot, patchKey));
        Directory.CreateDirectory(MpqDir(stackRoot, patchKey));
        Directory.CreateDirectory(ConfigDir(stackRoot, patchKey));
        Directory.CreateDirectory(PatchLuaDir(stackRoot, patchKey));
        Directory.CreateDirectory(PatchNewsDir(stackRoot, patchKey));
        SeedPatchDescriptionIfMissing(stackRoot, patchKey);
    }

    public static string PatchNewsDir(string stackRoot, string patchKey) =>
        Path.Combine(PatchDir(stackRoot, patchKey), "news");

    /// <summary>Writes the default placeholder description for built-in expansion patches when none exists.</summary>
    public static void SeedPatchDescriptionIfMissing(string stackRoot, string patchKey)
    {
        if (!DefaultPatchDescriptions.TryGetValue(patchKey, out var placeholder))
        {
            return;
        }

        var patchDir = PatchDir(stackRoot, patchKey);
        foreach (var name in PatchDescriptionFileNames)
        {
            if (File.Exists(Path.Combine(patchDir, name)))
            {
                return;
            }
        }

        File.WriteAllText(Path.Combine(patchDir, "description.txt"), placeholder);
    }

    /// <summary>Reads a patch-level description file, or the default placeholder / "no description".</summary>
    public static string ReadPatchDescription(string stackRoot, string patchKey)
    {
        var patchDir = PatchDir(stackRoot, patchKey);
        foreach (var name in PatchDescriptionFileNames)
        {
            var path = Path.Combine(patchDir, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return DefaultPatchDescriptions.TryGetValue(patchKey, out var placeholder)
            ? placeholder
            : "no description";
    }

    /// <summary>Returns the existing description file name for a patch, preferring description.md.</summary>
    public static string? FindPatchDescriptionFileName(string stackRoot, string patchKey)
    {
        var patchDir = PatchDir(stackRoot, patchKey);
        foreach (var name in PatchDescriptionFileNames)
        {
            if (File.Exists(Path.Combine(patchDir, name)))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Writes a patch-level description to description.md or the existing description file.</summary>
    public static void SavePatchDescription(string stackRoot, string patchKey, string content)
    {
        var patchDir = PatchDir(stackRoot, patchKey);
        Directory.CreateDirectory(patchDir);

        var fileName = FindPatchDescriptionFileName(stackRoot, patchKey) ?? "description.md";
        var path = Path.Combine(patchDir, fileName);
        var trimmed = content.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        File.WriteAllText(path, trimmed);
    }

    public static bool IsPatchDescriptionFile(string fileName) =>
        PatchDescriptionFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    private const string PatchTemplateDescription = """
        # My Patch

        Describe what this patch changes (SQL, DBC, maps, MPQ, etc.).

        Name this folder using the patch index scheme:
        - `patch 1.0`, `patch 1.1`, `patch 1.3.142` for Classic (root 1)
        - `patch 2.0`, `patch 2.1`, … for The Burning Crusade (root 2)
        - `patch 3.0`, `patch 3.1`, … for Wrath of the Lich King (root 3)
        - `patch 4.0`, `patch 4.1`, … for custom content (root 4)

        An optional label may follow the index, e.g. `patch 1.1 custom_quests`.

        Place the folder under `classic/`, `tbc/`, `wotlk/`, or `custom/` in your collection zip. The index must
        match that expansion (classic → 1.x, tbc → 2.x, wotlk → 3.x, custom → 4.x).
        """;

    /// <summary>Builds a zip archive with one example patch folder and the standard empty sub-folders.</summary>
    public static byte[] CreatePatchTemplateArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            const string patchRoot = "classic/patch 1.1/";
            AddZipTextEntry(archive, patchRoot + "description.md", PatchTemplateDescription);

            foreach (var relativeDir in new[]
            {
                "sql/world/",
                "sql/auth/",
                "sql/characters/",
                "dbc/",
                "map/",
                "mpq/",
                "config/"
            })
            {
                AddZipDirectoryEntry(archive, patchRoot + relativeDir);
            }

            AddZipTextEntry(
                archive,
                patchRoot + "mpq/mpq.json",
                """
                {
                  "remove": ["patch-example.MPQ"]
                }
                """);
        }

        return stream.ToArray();
    }

    private static void AddZipDirectoryEntry(ZipArchive archive, string directoryPath)
    {
        var normalized = directoryPath.TrimEnd('/') + "/";
        archive.CreateEntry(normalized);
    }

    private static void AddZipTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
