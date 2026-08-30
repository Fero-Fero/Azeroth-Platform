using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Playerbot Dungeon Sim calls <c>DungeonClearControl::StartAutonomousClear</c>, which exists
/// only on TopHatMan's <c>auto-playerbots</c> branch — pin Dungeon Clear when this module is selected.
/// Its character SQL uses mysql-client <c>DELIMITER</c> / <c>CREATE PROCEDURE</c>, which AzerothCore
/// db-import cannot apply (stdin to <c>mysql</c>). Fresh installs also apply <c>updates/</c> before
/// <c>*_99_*force_*install.sql</c> creates the tables; those earlier files are stubbed because the
/// force-install already carries the full schema and seed data. Command SQL ships with
/// <c>USE azc_world_ashbringer;</c> from the author's private realm; that line is stripped.
/// </summary>
public sealed class PlayerbotDungeonSimInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-playerbot-dungeon-sim";
    public const string DungeonClearId = "mod-dungeon-clear";
    public const string DungeonClearAutonomousBranch = "auto-playerbots";

    /// <summary>
    /// Previous host/container rewrites left a comment or a truncated
    /// <c>CREATE TABLE IF NOT EXISTS).</c> plus <c>SELECT 1</c>. AC sends the file as one query, so MySQL
    /// errors near <c>'). SELECT 1'</c>.
    /// </summary>
    private const string TruncatedCreateTable = "CREATE TABLE IF NOT EXISTS).";

    /// <summary>
    /// No-op that does not print a result row. Older checkouts used <c>SELECT 1;</c>, which
    /// db-import logs as a bare <c>1</c>.
    /// </summary>
    internal const string SilentStub = "DO 0;\n";
    internal const string LegacySelectStub = "SELECT 1;\n";

    private static readonly Regex CreateTableIfNotExists = new(
        @"^[ \t]*CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+[`'\w][\s\S]*?;",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Upstream SQL is written against a private Ashbringer schema
    /// (<c>USE azc_world_ashbringer;</c>). db-import already selects <c>acore_world</c> /
    /// <c>acore_characters</c>, so the USE fails with ERROR 1049.
    /// </summary>
    private static readonly Regex UseStatement = new(
        @"^[ \t]*USE\s+[`'""]?[\w]+[`'""]?[ \t]*;[ \t]*\r?\n?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public string ModuleId => CatalogId;

    public ModuleCompileProfile Compile { get; } = new()
    {
        BranchPins =
        [
            new ModuleBranchPin
            {
                ModuleId = DungeonClearId,
                Branch = DungeonClearAutonomousBranch,
            },
        ],
    };

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModuleInstallContribution());

    public IReadOnlyList<string> PrepareCheckout(string moduleDir)
    {
        if (!Directory.Exists(moduleDir))
        {
            return [];
        }

        var rewritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(moduleDir, "*", SearchOption.AllDirectories)
                     .Prepend(moduleDir))
        {
            StubUpdatesBeforeForceInstall(dir, rewritten);
        }

        foreach (var file in Directory.EnumerateFiles(moduleDir, "*.sql", SearchOption.AllDirectories))
        {
            if (RewriteFile(file))
            {
                rewritten.Add(file);
            }
        }

        return rewritten.ToList();
    }

    /// <summary>
    /// Fresh db-import applies <c>updates/</c> in filename order and often skips module
    /// <c>base/</c>. This module puts CREATE TABLE in <c>*_99_*force_*install.sql</c>, so
    /// earlier files (INSERT/ALTER) fail with "table doesn't exist". Those earlier files are
    /// redundant: the force-install already has the full schema and seed data.
    /// </summary>
    private static void StubUpdatesBeforeForceInstall(string dir, ISet<string> rewritten)
    {
        var sqlFiles = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.sql")
            : [];
        var forceInstalls = sqlFiles
            .Where(path => IsForceInstallSql(Path.GetFileName(path)))
            .ToList();
        if (forceInstalls.Count == 0)
        {
            return;
        }

        var firstForce = forceInstalls
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .First();
        foreach (var file in sqlFiles)
        {
            var name = Path.GetFileName(file);
            if (string.Compare(name, firstForce, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            if (WriteStub(file))
            {
                rewritten.Add(file);
            }
        }
    }

    private static bool IsForceInstallSql(string fileName) =>
        fileName.Contains("_99_", StringComparison.OrdinalIgnoreCase)
        && fileName.Contains("force", StringComparison.OrdinalIgnoreCase)
        && fileName.Contains("install", StringComparison.OrdinalIgnoreCase);

    private static bool RewriteFile(string file)
    {
        var original = File.ReadAllText(file);
        var next = RewriteSql(original);
        if (!string.Equals(original, next, StringComparison.Ordinal))
        {
            File.WriteAllText(file, next);
            return true;
        }

        // Host is already the stub; still report it so a stale named volume is overwritten.
        return IsStub(next);
    }

    private static bool WriteStub(string file)
    {
        var original = File.ReadAllText(file);
        // Leave an already-applied SELECT 1 stub alone so AC's updates hash still matches.
        if (IsStub(original))
        {
            return true;
        }

        File.WriteAllText(file, SilentStub);
        return true;
    }

    internal static string RewriteSql(string sql)
    {
        if (IsStub(sql))
        {
            return NormalizeStub(sql);
        }

        // Previous host/container rewrites left a comment or a truncated
        // "CREATE TABLE IF NOT EXISTS)." plus SELECT 1. AC sends the file as one query, so MySQL
        // errors near "'). SELECT 1". Never try to salvage CREATE TABLE from those files.
        if (sql.Contains(TruncatedCreateTable, StringComparison.OrdinalIgnoreCase))
        {
            return SilentStub;
        }

        var next = sql;
        if (NeedsRewrite(next))
        {
            var tables = CreateTableIfNotExists.Matches(next)
                .Select(match => match.Value.Trim())
                .Where(value => value.Length > 0)
                .ToArray();
            next = tables.Length == 0
                ? SilentStub
                : string.Join("\n\n", tables) + "\n";
        }

        next = UseStatement.Replace(next, "");
        return string.IsNullOrWhiteSpace(next) ? SilentStub : next;
    }

    private static bool IsStub(string sql)
    {
        var trimmed = sql.Replace("\r\n", "\n").Trim();
        return string.Equals(trimmed, "DO 0;", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "SELECT 1;", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStub(string sql)
    {
        var trimmed = sql.Replace("\r\n", "\n").Trim();
        return string.Equals(trimmed, "SELECT 1;", StringComparison.OrdinalIgnoreCase)
            ? LegacySelectStub
            : SilentStub;
    }

    private static bool NeedsRewrite(string sql) =>
        Regex.IsMatch(sql, @"^\s*DELIMITER\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)
        || Regex.IsMatch(sql, @"CREATE\s+PROCEDURE", RegexOptions.IgnoreCase)
        || sql.Contains(TruncatedCreateTable, StringComparison.OrdinalIgnoreCase);
}
