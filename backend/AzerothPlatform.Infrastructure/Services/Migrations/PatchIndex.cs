using System.Text.RegularExpressions;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>Semantic depth of a patch index: expansion (1), patch release (1.1), or hotfix (1.1.1).</summary>
public enum PatchTier
{
    Expansion,
    Patch,
    Hotfix
}

/// <summary>
/// Semantic patch version: expansion root 1/2/3 (classic/tbc/wotlk) plus up to two optional
/// sub-segments (e.g. 1, 1.1, 1.3.142). Encoded to <see cref="int"/> for persistence in
/// <c>AppliedPatchLevel</c> without a schema migration.
/// </summary>
public readonly struct PatchIndex : IComparable<PatchIndex>, IEquatable<PatchIndex>
{
    public const int MaxSegment = 999;
    public const int MaxComponents = 3;

    private static readonly Regex IndexRegex = new(
        @"^(?<root>[1-4])(?:\.(?<sub1>\d{1,3}))?(?:\.(?<sub2>\d{1,3}))?$",
        RegexOptions.CultureInvariant);

    private readonly int _c0;
    private readonly int _c1;
    private readonly int _c2;
    public int ComponentCount { get; }

    public PatchIndex(int root, int sub1 = 0, int sub2 = 0, bool explicitSub1 = false)
    {
        ValidateSegment(root, nameof(root));
        if (root is < 1 or > 4)
        {
            throw new ArgumentException("Patch index must start with 1 (classic), 2 (tbc), 3 (wotlk), or 4 (custom).");
        }

        if (sub2 > 0)
        {
            ValidateSegment(sub1, nameof(sub1));
            ValidateSegment(sub2, nameof(sub2));
            _c0 = root;
            _c1 = sub1;
            _c2 = sub2;
            ComponentCount = 3;
            return;
        }

        if (sub1 > 0 || explicitSub1)
        {
            ValidateSegment(sub1, nameof(sub1));
            _c0 = root;
            _c1 = sub1;
            _c2 = 0;
            ComponentCount = 2;
            return;
        }

        _c0 = root;
        _c1 = 0;
        _c2 = 0;
        ComponentCount = 1;
    }

    public int ExpansionRoot => _c0;

    public int Sub1 => _c1;

    public int Sub2 => _c2;

    /// <summary>
    /// First patch of an expansion: <c>1</c>, <c>1.0</c>, <c>2.0</c>, <c>3.0</c>.
    /// Not <c>1.1</c> or a hotfix such as <c>1.0.1</c>.
    /// </summary>
    public bool IsExpansionBaseline =>
        _c1 == 0 && _c2 == 0 && ComponentCount is 1 or 2;

    public static PatchIndex ComputeNext(
        PatchTier tier,
        int expansionRoot,
        IReadOnlyList<PatchIndex> existing,
        string? parentIndexRaw = null)
    {
        if (expansionRoot is < 1 or > 4)
        {
            throw new ArgumentException("Expansion root must be 1 (classic), 2 (tbc), 3 (wotlk), or 4 (custom).");
        }

        var sameRoot = existing.Where(index => index.ExpansionRoot == expansionRoot).ToList();

        return tier switch
        {
            PatchTier.Expansion => ComputeNextExpansion(expansionRoot, sameRoot),
            PatchTier.Patch => ComputeNextPatchRelease(expansionRoot, sameRoot),
            PatchTier.Hotfix => ComputeNextHotfix(expansionRoot, sameRoot, parentIndexRaw),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown patch tier.")
        };
    }

    /// <summary>Next append-import index: increments the second segment under the expansion root (1.1, 2.3, …).</summary>
    public static PatchIndex ComputeNextAppendImportIndex(int expansionRoot, IReadOnlyList<PatchIndex> existing)
    {
        var maxSub1 = existing
            .Where(index => index.ExpansionRoot == expansionRoot && index.ComponentCount >= 2)
            .Select(index => index.Sub1)
            .DefaultIfEmpty(0)
            .Max();
        return new PatchIndex(expansionRoot, maxSub1 + 1);
    }

    private static PatchIndex ComputeNextExpansion(int expansionRoot, IReadOnlyList<PatchIndex> existing)
    {
        if (existing.Any(index => HasExpansionEntryPoint(index)))
        {
            throw new InvalidOperationException(
                $"Expansion patch '{expansionRoot}.0' already exists. Create a patch or hotfix instead.");
        }

        return new PatchIndex(expansionRoot, 0, explicitSub1: true);
    }

    private static bool HasExpansionEntryPoint(PatchIndex index) =>
        index.ComponentCount == 1 || (index.ComponentCount >= 2 && index.Sub1 == 0 && index.Sub2 == 0);

    private static PatchIndex ComputeNextPatchRelease(int expansionRoot, IReadOnlyList<PatchIndex> existing)
    {
        var maxSub1 = existing
            .Where(index => index.ComponentCount >= 2)
            .Select(index => index.Sub1)
            .DefaultIfEmpty(0)
            .Max();
        return new PatchIndex(expansionRoot, maxSub1 + 1);
    }

    private static PatchIndex ComputeNextHotfix(
        int expansionRoot,
        IReadOnlyList<PatchIndex> existing,
        string? parentIndexRaw)
    {
        int parentSub1;
        if (!string.IsNullOrWhiteSpace(parentIndexRaw))
        {
            var parent = Parse(parentIndexRaw.Trim());
            if (parent.ExpansionRoot != expansionRoot || parent.ComponentCount < 2)
            {
                throw new ArgumentException(
                    $"Parent index '{parentIndexRaw}' must be a patch release under expansion root {expansionRoot} (e.g. {expansionRoot}.1).");
            }

            parentSub1 = parent.Sub1;
        }
        else
        {
            parentSub1 = existing
                .Where(index => index.ComponentCount >= 2)
                .Select(index => index.Sub1)
                .DefaultIfEmpty(1)
                .Max();
        }

        var maxSub2 = existing
            .Where(index => index.ComponentCount == 3 && index.Sub1 == parentSub1)
            .Select(index => index.Sub2)
            .DefaultIfEmpty(0)
            .Max();
        return new PatchIndex(expansionRoot, parentSub1, maxSub2 + 1);
    }

    public static PatchTier ParseTier(string? raw)
    {
        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "expansion" => PatchTier.Expansion,
            "patch" or "" => PatchTier.Patch,
            "hotfix" => PatchTier.Hotfix,
            _ => throw new ArgumentException("Kind must be one of: expansion, patch, hotfix.")
        };
    }

    public static PatchIndex Parse(string raw)
    {
        if (!TryParse(raw, out var index))
        {
            throw new ArgumentException(
                $"Invalid patch index '{raw}'. Expected 1–4 dot-separated numeric segments (e.g. 1, 1.1, 4.3.142) starting with 1, 2, 3, or 4.");
        }

        return index;
    }

    public static bool TryParse(string? raw, out PatchIndex index, bool explicitSub1 = false)
    {
        index = default;
        var trimmed = (raw ?? string.Empty).Trim();
        var match = IndexRegex.Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        var root = int.Parse(match.Groups["root"].Value);
        var hasSub1 = match.Groups["sub1"].Success;
        var sub1 = hasSub1 ? int.Parse(match.Groups["sub1"].Value) : 0;
        var sub2 = match.Groups["sub2"].Success ? int.Parse(match.Groups["sub2"].Value) : 0;
        index = new PatchIndex(root, sub1, sub2, explicitSub1: explicitSub1 || hasSub1);
        return true;
    }

    public static PatchIndex FromEncodedLevel(int encoded)
    {
        if (encoded <= 0)
        {
            return default;
        }

        var c0 = encoded / 1_000_000;
        var rem = encoded % 1_000_000;
        var c1 = rem / 1_000;
        var c2 = rem % 1_000;
        return new PatchIndex(c0, c1, c2);
    }

    public int ToEncodedLevel() => _c0 * 1_000_000 + _c1 * 1_000 + _c2;

    public string ToIndexString() => ComponentCount switch
    {
        1 => _c0.ToString(),
        2 => $"{_c0}.{_c1}",
        3 => $"{_c0}.{_c1}.{_c2}",
        _ => _c0.ToString()
    };

    /// <summary>Assigns the next sub-version under the same expansion root (1 → 1.1, 1.2 → 1.3, 1.1.1 → 1.1.2).</summary>
    public PatchIndex IncrementLast()
    {
        return ComponentCount switch
        {
            1 => new PatchIndex(_c0, 1),
            2 => new PatchIndex(_c0, _c1 + 1),
            3 => new PatchIndex(_c0, _c1, _c2 + 1),
            _ => new PatchIndex(_c0, 1)
        };
    }

    public void AssertMatchesExpansion(string expansion)
    {
        var expected = MigrationLayout.ExpansionRoot(expansion);
        if (ExpansionRoot != expected)
        {
            throw new ArgumentException(
                $"Patch index '{ToIndexString()}' belongs to {MigrationLayout.ExpansionName(ExpansionRoot)}, not {expansion}.");
        }
    }

    public int CompareTo(PatchIndex other)
    {
        var c = _c0.CompareTo(other._c0);
        if (c != 0) return c;
        c = _c1.CompareTo(other._c1);
        return c != 0 ? c : _c2.CompareTo(other._c2);
    }

    public bool Equals(PatchIndex other) =>
        _c0 == other._c0 && _c1 == other._c1 && _c2 == other._c2;

    public override bool Equals(object? obj) => obj is PatchIndex other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_c0, _c1, _c2);

    public static bool operator <(PatchIndex left, PatchIndex right) => left.CompareTo(right) < 0;
    public static bool operator >(PatchIndex left, PatchIndex right) => left.CompareTo(right) > 0;
    public static bool operator <=(PatchIndex left, PatchIndex right) => left.CompareTo(right) <= 0;
    public static bool operator >=(PatchIndex left, PatchIndex right) => left.CompareTo(right) >= 0;

    private static void ValidateSegment(int value, string paramName)
    {
        if (value is < 0 or > MaxSegment)
        {
            throw new ArgumentOutOfRangeException(paramName, $"Patch index segments must be between 0 and {MaxSegment}.");
        }
    }
}

/// <summary>Parses and formats on-disk / import patch folder names: <c>patch {index} {optional name}</c>.</summary>
public static partial class PatchFolderNames
{
    [GeneratedRegex(
        @"^patch\s+(?<index>[1-4](?:\.\d{1,3}){0,2})(?:\s+(?<name>.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolderRegex();

    public static bool TryParse(string? folderName, out PatchIndex index, out string? displayName)
    {
        index = default;
        displayName = null;
        var trimmed = (folderName ?? string.Empty).Trim();
        var match = FolderRegex().Match(trimmed);
        if (!match.Success || !PatchIndex.TryParse(match.Groups["index"].Value, out index))
        {
            return false;
        }

        if (match.Groups["name"].Success)
        {
            displayName = match.Groups["name"].Value.Trim();
            if (displayName.Length == 0)
            {
                displayName = null;
            }
        }

        return true;
    }

    public static string Format(PatchIndex index, string? displayName = null)
    {
        var trimmed = displayName?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? $"patch {index.ToIndexString()}"
            : $"patch {index.ToIndexString()} {trimmed}";
    }

    public static PatchIndex ParseFolder(string folderName)
    {
        if (!TryParse(folderName, out var index, out _))
        {
            throw new ArgumentException(
                $"Patch folder '{folderName}' must be named 'patch {{index}}' or 'patch {{index}} {{name}}' (e.g. patch 1, patch 1.1, patch 2 my_content).");
        }

        return index;
    }
}
