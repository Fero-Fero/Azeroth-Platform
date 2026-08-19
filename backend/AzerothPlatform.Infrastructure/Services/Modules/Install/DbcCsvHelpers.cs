using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public static class DbcTrimHelper
{
    /// <summary>
    /// Writes a difference CSV of <paramref name="moduleCsvPath"/> against <paramref name="baselineCsvPath"/>.
    /// Returns false when the result is empty (file deleted).
    /// </summary>
    public static async Task<bool> TrimAsync(
        string moduleCsvPath,
        string baselineCsvPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(moduleCsvPath))
        {
            return false;
        }

        var moduleText = await File.ReadAllTextAsync(moduleCsvPath, cancellationToken);
        var moduleLines = SplitRows(moduleText);
        if (moduleLines.Count == 0)
        {
            File.Delete(moduleCsvPath);
            return false;
        }

        Dictionary<string, string> baseline = new(StringComparer.Ordinal);
        if (File.Exists(baselineCsvPath))
        {
            var baselineLines = SplitRows(await File.ReadAllTextAsync(baselineCsvPath, cancellationToken));
            foreach (var line in baselineLines.Skip(1))
            {
                var id = CsvNormalizer.FirstCsvField(line);
                if (id.Length > 0)
                {
                    baseline[id] = NormalizeRow(line);
                }
            }
        }

        var kept = new List<string> { moduleLines[0] };
        foreach (var line in moduleLines.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = CsvNormalizer.FirstCsvField(line);
            if (id.Length == 0)
            {
                continue;
            }

            var normalized = NormalizeRow(line);
            if (baseline.TryGetValue(id, out var existing) && existing == normalized)
            {
                continue;
            }

            kept.Add(line);
        }

        if (kept.Count <= 1)
        {
            File.Delete(moduleCsvPath);
            return false;
        }

        await CsvNormalizer.WriteCrlfAsync(moduleCsvPath, string.Join("\r\n", kept), cancellationToken);
        return true;
    }

    private static List<string> SplitRows(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized
            .Split('\n', StringSplitOptions.None)
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string NormalizeRow(string line) => line.TrimEnd('\r', '\n').TrimEnd();
}

public static class DbcCoalesceHelper
{
    public sealed record CoalescedTable(string TableName, string CsvText);

    public static async Task<IReadOnlyList<CoalescedTable>> CoalesceAsync(
        IReadOnlyList<(string ModuleId, string CsvPath)> sources,
        CancellationToken cancellationToken = default)
    {
        var byTable = new Dictionary<string, List<(string ModuleId, string Header, List<(string Id, string Line)> Rows)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (moduleId, csvPath) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(csvPath))
            {
                continue;
            }

            var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(csvPath));
            var lines = (await File.ReadAllTextAsync(csvPath, cancellationToken))
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0)
                .ToList();
            if (lines.Count <= 1)
            {
                continue;
            }

            var rows = lines.Skip(1)
                .Select(line => (Id: CsvNormalizer.FirstCsvField(line), Line: line))
                .Where(row => row.Id.Length > 0)
                .ToList();

            if (!byTable.TryGetValue(table, out var list))
            {
                list = [];
                byTable[table] = list;
            }

            list.Add((moduleId, lines[0], rows));
        }

        var result = new List<CoalescedTable>();
        foreach (var (table, contributions) in byTable)
        {
            var seen = new Dictionary<string, (string ModuleId, string Line)>(StringComparer.Ordinal);
            string? header = null;
            foreach (var contribution in contributions)
            {
                header ??= contribution.Header;
                foreach (var (id, line) in contribution.Rows)
                {
                    if (seen.TryGetValue(id, out var existing))
                    {
                        if (!string.Equals(Normalize(existing.Line), Normalize(line), StringComparison.Ordinal))
                        {
                            throw new ModuleDbcConflictException(existing.ModuleId, contribution.ModuleId, table, id);
                        }

                        continue;
                    }

                    seen[id] = (contribution.ModuleId, line);
                }
            }

            if (header is null || seen.Count == 0)
            {
                continue;
            }

            var body = new List<string> { header };
            body.AddRange(seen.Values.Select(value => value.Line));
            result.Add(new CoalescedTable(table, CsvNormalizer.EnsureTrailingCrlf(string.Join("\r\n", body))));
        }

        return result;
    }

    private static string Normalize(string line) => line.TrimEnd('\r', '\n').TrimEnd();
}
