using System.IO.Compression;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services.IndividualProgression;
using AzerothPlatform.Infrastructure.Services.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class IndividualProgressionHeaderParserTests
{
    [Fact]
    public void ParseHeader_reads_progression_enum_entries()
    {
        const string header = """
            enum ProgressionState
            {
                PROGRESSION_START = 0,
                PROGRESSION_MOLTEN_CORE = 1,
                PROGRESSION_ONYXIA = 2,
            };
            """;

        var parsed = InvokeParseHeader(header);
        parsed.Should().HaveCount(3);
        parsed[0].State.Should().Be(0);
        parsed[0].Slug.Should().Be("START");
        parsed[1].Slug.Should().Be("MOLTEN_CORE");
    }

    private static List<IndividualProgressionHeaderParser.ParsedState> InvokeParseHeader(string content)
    {
        var method = typeof(IndividualProgressionHeaderParser)
            .GetMethod("ParseHeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (List<IndividualProgressionHeaderParser.ParsedState>)method!.Invoke(null, [content])!;
    }
}

public sealed class IndividualProgressionSyncLogicTests
{
    [Fact]
    public void ResolveProgressionRepoDirectory_uses_stack_absolute_path()
    {
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-repo-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, "my-stack");
        try
        {
            var service = CreateSyncServiceForRepoResolution(buildsPath);
            InvokeResolveProgressionRepoDirectory(service, stackRoot)
                .Should()
                .Be(Path.GetFullPath(Path.Combine(stackRoot, MigrationLayout.ProgressionRepoDirName)));
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(0, "classic", false, "0")]
    [InlineData(1, "classic", true, null)]
    [InlineData(8, "tbc", true, "1")]
    [InlineData(14, "wotlk", true, "2")]
    public void ResolveExpansionForPatch_sets_boundaries(int state, string expansion, bool increments, string? expectedExpansion)
    {
        var metadata = new PatchProgressionMetadataDto
        {
            State = state,
            Slug = "TEST",
            Expansion = expansion,
            IncrementsProgression = increments,
        };

        var result = InvokeResolveExpansion(metadata);
        result.Should().Be(expectedExpansion);
    }

    [Fact]
    public void IncrementConfigValue_increments_parsed_integer()
    {
        var settings = new IndividualProgressionSettingsDto
        {
            Values = { ["IndividualProgression.ProgressionLimit"] = "3" },
        };

        var result = InvokeIncrement(settings, "IndividualProgression.ProgressionLimit");
        result.Should().Be("4");
    }

    private static string? InvokeResolveExpansion(PatchProgressionMetadataDto metadata)
    {
        var method = typeof(IndividualProgressionSyncService)
            .GetMethod("ResolveExpansionForPatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string?)method!.Invoke(null, [metadata]);
    }

    private static string InvokeResolveProgressionRepoDirectory(IndividualProgressionSyncService service, string stackRoot)
    {
        var method = typeof(IndividualProgressionSyncService)
            .GetMethod("ResolveProgressionRepoDirectory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, [stackRoot])!;
    }

    private static IndividualProgressionSyncService CreateSyncServiceForRepoResolution(string buildsPath)
    {
        var docker = Options.Create(new DockerOptions { BuildsPath = buildsPath });
        var migrations = Options.Create(new MigrationOptions());
        var serverConfig = new Mock<IServerConfigService>();
        var httpClientFactory = new Mock<IHttpClientFactory>();

        return new IndividualProgressionSyncService(
            CreateInMemoryDbContext(),
            serverConfig.Object,
            httpClientFactory.Object,
            docker,
            migrations,
            NullLogger<IndividualProgressionSyncService>.Instance);
    }

    private static AzerothCoreDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AzerothCoreDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AzerothCoreDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static string InvokeIncrement(IndividualProgressionSettingsDto settings, string key)
    {
        var method = typeof(IndividualProgressionSyncService)
            .GetMethod("IncrementConfigValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, [settings, key])!;
    }
}

public sealed class ProgressionRepoStructureValidatorTests
{
    [Theory]
    [InlineData("sql/character", "sql/characters")]
    [InlineData("sql/character/foo.sql", "sql/characters/foo.sql")]
    [InlineData("sql/world", "sql/world")]
    [InlineData("config", "config")]
    public void NormalizeRepoCategoryPath_maps_character_database(string input, string expected)
    {
        ProgressionRepoStructureValidator.NormalizeRepoCategoryPath(input).Should().Be(expected);
    }

    [Fact]
    public void Validate_reports_missing_stack_directories()
    {
        using var repo = new TempDirectory();
        using var stack = new TempDirectory();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.0 Start");

        var errors = new List<string>();
        ProgressionRepoStructureValidator.Validate(stack.Path, repo.Path, errors);

        errors.Should().Contain(error => error.Contains("No stack patch matches reference patch"));
    }

    [Fact]
    public void Validate_matches_pre_patch_folders_by_repo_name()
    {
        using var repo = new TempDirectory();
        using var stack = new TempDirectory();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.9 Pre Patch");
        CreateStackPatch(stack.Path, "patch 1.9 Pre Patch", includeConfig: true);

        var errors = new List<string>();
        ProgressionRepoStructureValidator.Validate(stack.Path, repo.Path, errors);

        errors.Where(error => error.Contains("1.9 Pre Patch", StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Validate_passes_when_stack_matches_reference_layout()
    {
        using var repo = new TempDirectory();
        using var stack = new TempDirectory();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.0 Start");
        CreateStackPatch(stack.Path, "patch 1.0 Start", includeConfig: true);

        var errors = new List<string>();
        ProgressionRepoStructureValidator.Validate(stack.Path, repo.Path, errors);

        errors.Where(error => error.Contains("patch 1.0 Start", StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeEmpty();
    }

    private static void EnsureExpansionRoots(string repoRoot)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "Classic"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Tbc"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Wotlk"));
    }

    private static void CreateReferencePatch(string repoRoot, string expansion, string patchName)
    {
        var patchDir = Path.Combine(repoRoot, expansion, patchName);
        Directory.CreateDirectory(Path.Combine(patchDir, "config"));
        Directory.CreateDirectory(Path.Combine(patchDir, "script"));
        Directory.CreateDirectory(Path.Combine(patchDir, "sql", "world"));
        Directory.CreateDirectory(Path.Combine(patchDir, "sql", "auth"));
        Directory.CreateDirectory(Path.Combine(patchDir, "sql", "character"));
        Directory.CreateDirectory(Path.Combine(patchDir, "dbc"));
        Directory.CreateDirectory(Path.Combine(patchDir, "map"));
        Directory.CreateDirectory(Path.Combine(patchDir, "mpq"));
        File.WriteAllText(Path.Combine(patchDir, "description.md"), "Reference patch");
        File.WriteAllText(Path.Combine(patchDir, "config", "worldserver.json"), "{}");
        File.WriteAllText(Path.Combine(patchDir, "mpq", "mpq.json"), "{}");
    }

    private static void CreateStackPatch(string stackRoot, string patchKey, bool includeConfig)
    {
        var patchDir = Path.Combine(stackRoot, "migrations", patchKey);
        Directory.CreateDirectory(Path.Combine(patchDir, "script"));
        Directory.CreateDirectory(Path.Combine(patchDir, "sql", "world"));
        Directory.CreateDirectory(Path.Combine(patchDir, "sql", "auth"));
        Directory.CreateDirectory(Path.Combine(patchDir, "sql", "characters"));
        Directory.CreateDirectory(Path.Combine(patchDir, "dbc"));
        Directory.CreateDirectory(Path.Combine(patchDir, "map"));
        Directory.CreateDirectory(Path.Combine(patchDir, "mpq"));
        File.WriteAllText(Path.Combine(patchDir, "description.md"), "Stack patch");
        File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");

        if (includeConfig)
        {
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
            File.WriteAllText(Path.Combine(patchDir, "config", "worldserver.json"), "{}");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class PatchConfigOverrideReaderTests
{
    [Fact]
    public void ReadOverrides_parses_json_files_and_skips_empty_placeholders()
    {
        var stackRoot = Path.Combine(Path.GetTempPath(), "azp-config-overrides-" + Guid.NewGuid().ToString("N"));
        var patchKey = "patch 1.0 Start";
        try
        {
            var patchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
            File.WriteAllText(
                Path.Combine(patchDir, "config", "worldserver.json"),
                """{"Expansion":"1","Rate.XP.Kill":"2"}""");
            File.WriteAllText(
                Path.Combine(patchDir, "config", "empty.json"),
                "// Optional overrides\n");

            var etcDir = MigrationLayout.EtcDir(stackRoot);
            Directory.CreateDirectory(etcDir);
            File.WriteAllText(Path.Combine(etcDir, "worldserver.conf"), "Expansion = 0\n");

            var overrides = PatchConfigOverrideReader.ReadOverrides(stackRoot, patchKey);

            overrides.Should().HaveCount(2);
            overrides.Should().Contain(entry =>
                entry.SourceJson == "config/worldserver.json"
                && entry.TargetConf == "worldserver.conf"
                && entry.Key == "Expansion"
                && entry.Value == "1");
            overrides.Should().Contain(entry => entry.Key == "Rate.XP.Kill" && entry.Value == "2");
        }
        finally
        {
            if (Directory.Exists(stackRoot))
            {
                Directory.Delete(stackRoot, recursive: true);
            }
        }
    }
}

public sealed class PatchConfigValidatorTests
{
    [Fact]
    public async Task ValidateAsync_reports_missing_server_config_and_keys()
    {
        var stackId = "patch-config-validate";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-patch-config-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            var patchKey = "patch 1.0 Start";
            var patchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
            File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");
            File.WriteAllText(
                Path.Combine(patchDir, "config", "worldserver.json"),
                """{"Expansion":"2","MissingKey":"1"}""");

            var etcDir = MigrationLayout.EtcDir(stackRoot);
            Directory.CreateDirectory(etcDir);
            File.WriteAllText(Path.Combine(etcDir, "worldserver.conf"), "Expansion = 0\n");

            var serverConfig = new Mock<IServerConfigService>();
            serverConfig
                .Setup(s => s.ReadAsync(stackId, "worldserver.conf", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ServerConfigContentDto
                {
                    Path = "worldserver.conf",
                    Content = "Expansion = 0\n",
                });

            var errors = new List<string>();
            var keyChecks = new List<IndividualProgressionKeyCheckDto>();
            await PatchConfigValidator.ValidateAsync(
                stackId,
                stackRoot,
                serverConfig.Object,
                errors,
                keyChecks,
                CancellationToken.None);

            errors.Should().Contain(error => error.Contains("MissingKey"));
            keyChecks.Should().Contain(check =>
                check.PatchKey == patchKey
                && check.Key == "Expansion"
                && check.Exists);
            keyChecks.Should().Contain(check =>
                check.Key == "MissingKey"
                && !check.Exists);
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }
    [Fact]
    public async Task ValidateAsync_ignores_empty_and_comment_only_config_files()
    {
        var stackId = "patch-config-empty";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-patch-config-empty-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            var patchKey = "patch 1.0 Start";
            var patchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
            File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");
            File.WriteAllText(Path.Combine(patchDir, "config", "worldserver.json"), "// Optional overrides\n");
            File.WriteAllText(Path.Combine(patchDir, "config", "empty.json"), "   \n");

            var errors = new List<string>();
            var keyChecks = new List<IndividualProgressionKeyCheckDto>();
            await PatchConfigValidator.ValidateAsync(
                stackId,
                stackRoot,
                new Mock<IServerConfigService>().Object,
                errors,
                keyChecks,
                CancellationToken.None);

            errors.Should().BeEmpty();
            keyChecks.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidateAsync_fails_on_invalid_json_with_real_content()
    {
        var stackId = "patch-config-invalid";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-patch-config-invalid-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            var patchKey = "patch 1.0 Start";
            var patchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
            File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");
            File.WriteAllText(
                Path.Combine(patchDir, "config", "worldserver.json"),
                """{"Expansion": "0", broken json""");

            var errors = new List<string>();
            var keyChecks = new List<IndividualProgressionKeyCheckDto>();
            await PatchConfigValidator.ValidateAsync(
                stackId,
                stackRoot,
                new Mock<IServerConfigService>().Object,
                errors,
                keyChecks,
                CancellationToken.None);

            errors.Should().ContainSingle(error =>
                error.Contains("failed to parse config/worldserver.json", StringComparison.OrdinalIgnoreCase));
            keyChecks.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }
}

public sealed class ProgressionPatchNamingTests
{
    [Theory]
    [InlineData("2.1 Serpentshrine Cavern", "patch 2.1 Serpentshrine Cavern")]
    [InlineData("1.9 Pre Patch", "patch 1.9 Pre Patch")]
    [InlineData("3.5 Ruby Sanctum", "patch 3.5 Ruby Sanctum")]
    public void TryFormatPatchKey_uses_repo_folder_label(string repoFolder, string expectedPatchKey)
    {
        ProgressionPatchNaming.TryFormatPatchKey(repoFolder, out var patchKey).Should().BeTrue();
        patchKey.Should().Be(expectedPatchKey);
    }
}

public sealed class ProgressionRepoPatchSeederTests
{
    [Fact]
    public void Seed_uses_repo_folder_names_instead_of_catalog_slugs()
    {
        using var repo = new TempDirectoryWrapper();
        using var stack = new TempDirectoryWrapper();

        EnsureExpansionRoots(repo.Path);
        Directory.CreateDirectory(Path.Combine(repo.Path, "Tbc", "2.1 Serpentshrine Cavern", "config"));
        File.WriteAllText(
            Path.Combine(repo.Path, "Tbc", "2.1 Serpentshrine Cavern", "description.md"),
            "Reference patch");

        ProgressionRepoPatchSeeder.Seed(repo.Path, stack.Path, onlyMissing: false);

        Directory.Exists(Path.Combine(stack.Path, "migrations", "patch 2.1 Serpentshrine Cavern"))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void Seed_creates_stack_patches_from_progression_repository_layout()
    {
        using var repo = new TempDirectoryWrapper();
        using var stack = new TempDirectoryWrapper();

        EnsureExpansionRoots(repo.Path);
        var referencePatch = Path.Combine(repo.Path, "Classic", "1.0 Start");
        Directory.CreateDirectory(Path.Combine(referencePatch, "config"));
        File.WriteAllText(Path.Combine(referencePatch, "description.md"), "# Start");

        var created = ProgressionRepoPatchSeeder.Seed(repo.Path, stack.Path, onlyMissing: false);

        created.Should().Be(1);
        var stackPatchDir = Path.Combine(stack.Path, "migrations", "patch 1.0 Start");
        Directory.Exists(stackPatchDir).Should().BeTrue();
        File.Exists(Path.Combine(stackPatchDir, "description.md")).Should().BeTrue();
        File.Exists(Path.Combine(stackPatchDir, "progression.json")).Should().BeTrue();
    }

    [Fact]
    public void Seed_onlyMissing_skips_existing_patch_folders()
    {
        using var repo = new TempDirectoryWrapper();
        using var stack = new TempDirectoryWrapper();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.0 Start");
        CreateReferencePatch(repo.Path, "Classic", "1.1 Molten Core");
        CreateStackPatch(stack.Path, "patch 1.0 Start", includeConfig: true);

        var created = ProgressionRepoPatchSeeder.Seed(repo.Path, stack.Path, onlyMissing: true);

        created.Should().Be(1);
        Directory.Exists(Path.Combine(stack.Path, "migrations", "patch 1.1 Molten Core")).Should().BeTrue();
    }

    private static void EnsureExpansionRoots(string repoRoot)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "Classic"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Tbc"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Wotlk"));
    }

    private static void CreateReferencePatch(string repoRoot, string expansion, string patchName)
    {
        var patchDir = Path.Combine(repoRoot, expansion, patchName);
        Directory.CreateDirectory(Path.Combine(patchDir, "config"));
        File.WriteAllText(Path.Combine(patchDir, "description.md"), "Reference patch");
    }

    private static void CreateStackPatch(string stackRoot, string patchKey, bool includeConfig)
    {
        var patchDir = Path.Combine(stackRoot, "migrations", patchKey);
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");

        if (includeConfig)
        {
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
        }
    }

    private sealed class TempDirectoryWrapper : IDisposable
    {
        public TempDirectoryWrapper()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "migrations"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class ProgressionSyncProgressStoreTests
{
    [Fact]
    public void IsActivelyRunning_returns_false_for_stale_progress_file()
    {
        using var stack = new TempDirectoryWrapper();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        File.WriteAllText(
            ProgressionSyncProgressStore.ProgressPath(stack.Path),
            $$"""
              {
                "isRunning": true,
                "phase": "Copying progression repository",
                "progressPercent": 55,
                "message": "Copying files…",
                "startedAt": "{{startedAt:O}}",
                "log": []
              }
              """);
        File.SetLastWriteTimeUtc(
            ProgressionSyncProgressStore.ProgressPath(stack.Path),
            DateTime.UtcNow.AddMinutes(-10));

        var progress = ProgressionSyncProgressStore.TryLoadAsync(stack.Path).GetAwaiter().GetResult();
        progress.Should().NotBeNull();
        ProgressionSyncProgressStore.IsStale(progress!, stack.Path).Should().BeTrue();
        ProgressionSyncProgressStore.IsActivelyRunning(progress, stack.Path).Should().BeFalse();
    }

    [Fact]
    public void IsActivelyRunning_returns_true_for_recent_progress_updates()
    {
        using var stack = new TempDirectoryWrapper();
        var progressStore = new ProgressionSyncProgressStore(stack.Path);
        progressStore.StartAsync().GetAwaiter().GetResult();
        progressStore.ReportAsync("Testing", 50, "Still running…").GetAwaiter().GetResult();

        var progress = ProgressionSyncProgressStore.TryLoadAsync(stack.Path).GetAwaiter().GetResult();
        ProgressionSyncProgressStore.IsActivelyRunning(progress, stack.Path).Should().BeTrue();
    }

    private sealed class TempDirectoryWrapper : IDisposable
    {
        public TempDirectoryWrapper()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class ProgressionRepoAlignmentTests
{
    [Fact]
    public void Counts_use_repo_aligned_patch_keys()
    {
        using var repo = new TempDirectoryWrapper();
        using var stack = new TempDirectoryWrapper();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.0 Start");
        CreateReferencePatch(repo.Path, "Classic", "1.9 Pre Patch");
        CreateStackPatch(stack.Path, "patch 1.0 Start", includeMetadata: true);
        CreateStackPatch(stack.Path, "patch 1.1 MOLTEN_CORE", includeMetadata: true);

        ProgressionRepoAlignment.CountExpectedPatches(repo.Path).Should().Be(2);
        ProgressionRepoAlignment.CountAlignedPatches(repo.Path, stack.Path).Should().Be(1);
        ProgressionRepoAlignment.CountMissingPatches(repo.Path, stack.Path).Should().Be(1);
    }

    [Fact]
    public void RemoveOrphanedManagedPatches_deletes_legacy_catalog_folders()
    {
        using var repo = new TempDirectoryWrapper();
        using var stack = new TempDirectoryWrapper();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.0 Start");
        CreateStackPatch(stack.Path, "patch 1.0 Start", includeMetadata: true);
        CreateStackPatch(stack.Path, "patch 1.1 MOLTEN_CORE", includeMetadata: true);

        var removed = ProgressionRepoAlignment.RemoveOrphanedManagedPatches(repo.Path, stack.Path);

        removed.Should().Be(1);
        Directory.Exists(Path.Combine(stack.Path, "migrations", "patch 1.1 MOLTEN_CORE")).Should().BeFalse();
        Directory.Exists(Path.Combine(stack.Path, "migrations", "patch 1.0 Start")).Should().BeTrue();
    }

    private static void EnsureExpansionRoots(string repoRoot)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, "Classic"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Tbc"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Wotlk"));
    }

    private static void CreateReferencePatch(string repoRoot, string expansion, string patchName)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, expansion, patchName));
    }

    private static void CreateStackPatch(string stackRoot, string patchKey, bool includeMetadata)
    {
        var patchDir = Path.Combine(stackRoot, "migrations", patchKey);
        Directory.CreateDirectory(patchDir);
        if (includeMetadata)
        {
            File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");
        }
    }

    private sealed class TempDirectoryWrapper : IDisposable
    {
        public TempDirectoryWrapper()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "migrations"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class ProgressionSyncTargetPolicyTests
{
    [Fact]
    public void ShouldApplySyncToPath_allows_all_targets_on_initial_sync()
    {
        using var stack = new TempDirectoryWrapper();
        var patchDir = Path.Combine(stack.Path, "migrations", "patch 4.0 CUSTOM");
        Directory.CreateDirectory(patchDir);

        var log = new List<string>();
        ProgressionSyncTargetPolicy.ShouldApplySyncToPath(stack.Path, patchDir, initialSync: true, log)
            .Should().BeTrue();
        log.Should().BeEmpty();
    }

    [Fact]
    public void ShouldApplySyncToPath_skips_custom_patch_on_update_sync()
    {
        using var stack = new TempDirectoryWrapper();
        var patchDir = Path.Combine(stack.Path, "migrations", "patch 4.0 CUSTOM");
        Directory.CreateDirectory(patchDir);

        var log = new List<string>();
        ProgressionSyncTargetPolicy.ShouldApplySyncToPath(stack.Path, patchDir, initialSync: false, log)
            .Should().BeFalse();
        log.Should().ContainSingle(entry => entry.Contains("custom patch"));
    }

    [Fact]
    public void ShouldApplySyncToPath_allows_managed_progression_patch_on_update_sync()
    {
        using var stack = new TempDirectoryWrapper();
        var patchDir = Path.Combine(stack.Path, "migrations", "patch 1.2 ONYXIA");
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");

        var log = new List<string>();
        ProgressionSyncTargetPolicy.ShouldApplySyncToPath(stack.Path, patchDir, initialSync: false, log)
            .Should().BeTrue();
        log.Should().BeEmpty();
    }

    private sealed class TempDirectoryWrapper : IDisposable
    {
        public TempDirectoryWrapper()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "migrations"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class ProgressionPatchFolderResolverTests
{
    [Theory]
    [InlineData("mod-individual-progression/data/sql/world/base/*", "data/sql/world/base/*")]
    [InlineData("data/sql/world/base/*", "data/sql/world/base/*")]
    public void NormalizeModuleSourcePath_strips_redundant_prefix(string source, string expected)
    {
        ProgressionPatchFolderResolver.NormalizeModuleSourcePath(source).Should().Be(expected);
    }

    [Fact]
    public void MatchDefinition_maps_repo_wotlk_version_to_catalog_index()
    {
        var catalog = IndividualProgressionPatchCatalog.All;
        var match = ProgressionPatchFolderResolver.MatchDefinition("wotlk", "3.5 Ruby Sanctum", catalog);
        match.Should().NotBeNull();
        match!.Index.Should().Be("3.3");
        match.Slug.Should().Be("WOTLK_TIER_4");
    }

    [Fact]
    public void Resolve_maps_repo_destination_to_stack_patch_folder()
    {
        using var stack = new TempDirectoryWrapper();
        var patchDir = Path.Combine(stack.Path, "migrations", "patch 3.5 Ruby Sanctum");
        Directory.CreateDirectory(Path.Combine(patchDir, "config"));
        File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");

        var resolved = ProgressionPatchFolderResolver.Resolve(
            stack.Path,
            "Wotlk/3.5 Ruby Sanctum/config/");

        resolved.Should().Be(Path.Combine(patchDir, "config"));
    }

    [Fact]
    public void Resolve_is_case_insensitive_for_expansion_and_destination()
    {
        using var stack = new TempDirectoryWrapper();
        var patchDir = Path.Combine(stack.Path, "migrations", "patch 1.0 Start");
        Directory.CreateDirectory(Path.Combine(patchDir, "config"));
        File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");

        var resolved = ProgressionPatchFolderResolver.Resolve(
            stack.Path,
            "classic/1.0 Start/config/");

        // Destination ends with a file name; resolver returns the containing directory.
        resolved.Should().Be(Path.Combine(patchDir, "config"));
    }

    [Fact]
    public void Resolve_returns_null_for_repo_patches_without_stack_counterpart()
    {
        using var stack = new TempDirectoryWrapper();
        var patchDir = Path.Combine(stack.Path, "migrations", "patch 1.0 Start");
        Directory.CreateDirectory(patchDir);
        File.WriteAllText(Path.Combine(patchDir, "progression.json"), "{}");

        ProgressionPatchFolderResolver.Resolve(stack.Path, "Classic/1.9 Pre Patch/config/")
            .Should().BeNull();
    }

    private sealed class TempDirectoryWrapper : IDisposable
    {
        public TempDirectoryWrapper()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "migrations"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
