using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Client;

public sealed class ClientContainerServiceTests
{
    [Fact]
    public void BuildLoopbackMutatingCurlArgs_uses_container_env_token_as_one_sh_c_argument()
    {
        var args = ClientContainerService.BuildLoopbackMutatingCurlArgs(
            dockerContext: null,
            container: "acore-test-abc-client",
            endpoint: "rescan",
            port: 8090);

        args.Should().Equal(
            "exec",
            "acore-test-abc-client",
            "sh",
            "-c",
            "curl -fsS -X POST -H \"Authorization: Bearer ${CLIENT_AUTH_TOKEN}\" http://localhost:8090/rescan");
        args.Should().NotContain(a => a.Contains("Bearer ", StringComparison.Ordinal) && !a.Contains("${CLIENT_AUTH_TOKEN}", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLoopbackMutatingCurlArgs_prefixes_external_docker_context()
    {
        var args = ClientContainerService.BuildLoopbackMutatingCurlArgs(
            dockerContext: "stack-engine",
            container: "acore-test-abc-client",
            endpoint: "force-verify",
            port: 8090);

        args.Should().StartWith(new[] { "--context", "stack-engine", "exec" });
        args.Last().Should().Be(
            "curl -fsS -X POST -H \"Authorization: Bearer ${CLIENT_AUTH_TOKEN}\" http://localhost:8090/force-verify");
    }
}
