namespace AzerothPlatform.Infrastructure.Services.Stacks;

/// <summary>
/// The classic Docker builder (<c>DOCKER_BUILDKIT=0</c>) keeps the last Dockerfile <c>RUN</c>
/// container after a failed or cancelled image build. Those leftovers are unnamed
/// (adjective_scientist) and idle. Buildx's <c>buildx_buildkit_default</c> is a persistent
/// builder and is never selected.
/// </summary>
internal static class ClassicBuilderLeftovers
{
    /// <summary>
    /// Distinctive command from AzerothCore's compile <c>RUN</c>. Only exited containers whose
    /// command contains this are removed, so a concurrent in-progress build is left running.
    /// </summary>
    internal const string CompileCommandMarker = "cmake /azerothcore";

    internal const string DockerPsFormat = "{{.ID}}\t{{.Names}}\t{{.Command}}";

    /// <summary>
    /// Parses <c>docker ps -a --filter status=exited --no-trunc --format ID\\tNames\\tCommand</c>
    /// output into container ids that are safe to <c>docker rm</c>.
    /// </summary>
    internal static IReadOnlyList<string> IdsToRemove(string dockerPsOutput)
    {
        if (string.IsNullOrWhiteSpace(dockerPsOutput))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var line in dockerPsOutput.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var firstTab = trimmed.IndexOf('\t');
            if (firstTab <= 0)
            {
                continue;
            }

            var id = trimmed[..firstTab].Trim();
            var rest = trimmed[(firstTab + 1)..];
            var secondTab = rest.IndexOf('\t');
            var name = secondTab < 0 ? rest : rest[..secondTab];
            var command = secondTab < 0 ? string.Empty : rest[(secondTab + 1)..];

            if (name.Contains("buildx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (command.Contains(CompileCommandMarker, StringComparison.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
