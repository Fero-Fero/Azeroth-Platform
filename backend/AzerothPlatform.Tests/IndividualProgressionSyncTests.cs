using System.IO.Compression;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.IndividualProgression;
using FluentAssertions;
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
        CreateStackPatch(stack.Path, "patch 1.0 START", includeConfig: false);

        var errors = new List<string>();
        ProgressionRepoStructureValidator.Validate(stack.Path, repo.Path, errors);

        errors.Should().Contain(error => error.Contains("missing 'config/'"));
    }

    [Fact]
    public void Validate_passes_when_stack_matches_reference_layout()
    {
        using var repo = new TempDirectory();
        using var stack = new TempDirectory();

        EnsureExpansionRoots(repo.Path);
        CreateReferencePatch(repo.Path, "Classic", "1.0 Start");
        CreateStackPatch(stack.Path, "patch 1.0 START", includeConfig: true);

        var errors = new List<string>();
        ProgressionRepoStructureValidator.Validate(stack.Path, repo.Path, errors);

        errors.Where(error => error.Contains("patch 1.0 START", StringComparison.OrdinalIgnoreCase))
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
