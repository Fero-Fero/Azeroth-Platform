using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests.Stacks;

public sealed class StackRuntimeProbePolicyTests
{
    [Fact]
    public void LocalStartJob_StillProbesSoInitContainersShowAsRunning()
    {
        var skip = StackRuntimeProbePolicy.ShouldSkipLiveProbe(
            lifecycleJobRunning: true,
            DeploymentTarget.Local,
            StackStatus.Starting);

        Assert.False(skip);
    }

    [Fact]
    public void ExternalStartJob_SkipsProbeToAvoidSshContention()
    {
        var skip = StackRuntimeProbePolicy.ShouldSkipLiveProbe(
            lifecycleJobRunning: true,
            DeploymentTarget.External,
            StackStatus.Starting);

        Assert.True(skip);
    }

    [Theory]
    [InlineData(DeploymentTarget.Local)]
    [InlineData(DeploymentTarget.External)]
    public void SetupIncomplete_AlwaysSkipsProbe(DeploymentTarget target)
    {
        var skip = StackRuntimeProbePolicy.ShouldSkipLiveProbe(
            lifecycleJobRunning: false,
            target,
            StackStatus.SetupIncomplete);

        Assert.True(skip);
    }

    [Fact]
    public void IdleLocalStack_Probes()
    {
        var skip = StackRuntimeProbePolicy.ShouldSkipLiveProbe(
            lifecycleJobRunning: false,
            DeploymentTarget.Local,
            StackStatus.Stopped);

        Assert.False(skip);
    }
}
