using AzerothPlatform.Core.Modules;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class OllamaSidecarTests
{
    [Theory]
    [InlineData(null, GpuBackend.Cpu, OllamaSidecar.Image)]
    [InlineData("", GpuBackend.Nvidia, OllamaSidecar.Image)]
    [InlineData(OllamaSidecar.Image, GpuBackend.Vulkan, OllamaSidecar.Image)]
    [InlineData(OllamaSidecar.Image, GpuBackend.Rocm, OllamaSidecar.RocmImage)]
    [InlineData("ollama/ollama", GpuBackend.Rocm, OllamaSidecar.RocmImage)]
    [InlineData("ghcr.io/example/ollama:custom", GpuBackend.Rocm, "ghcr.io/example/ollama:custom")]
    public void ResolveImage_picks_rocm_tag_only_for_default_hub_image(
        string? requested,
        GpuBackend backend,
        string expected)
    {
        Assert.Equal(expected, OllamaSidecar.ResolveImage(requested, backend));
    }

    [Theory]
    [InlineData("llama3.2:1b", "models/manifests/registry.ollama.ai/library/llama3.2/1b")]
    [InlineData("llama3.2", "models/manifests/registry.ollama.ai/library/llama3.2/latest")]
    [InlineData("org/custom:tag", "models/manifests/registry.ollama.ai/org/custom/tag")]
    public void LibraryManifestRelativePath_maps_library_and_namespaced_models(
        string model,
        string expected)
    {
        Assert.Equal(expected, OllamaSidecar.LibraryManifestRelativePath(model));
    }

    [Theory]
    [InlineData(OllamaSidecar.ChatModuleId, true)]
    [InlineData(OllamaSidecar.BuddyModuleId, true)]
    [InlineData(LlmChatterBridge.ModuleId, false)]
    [InlineData("mod-playerbots", false)]
    [InlineData(null, false)]
    public void ReplacesPlayerbotsChatter_excludes_llm_chatter(string? moduleId, bool expected)
    {
        Assert.Equal(expected, OllamaSidecar.ReplacesPlayerbotsChatter(moduleId));
    }

    [Theory]
    [InlineData(OllamaSidecar.ChatModuleId, true)]
    [InlineData(OllamaSidecar.BuddyModuleId, true)]
    [InlineData(LlmChatterBridge.ModuleId, true)]
    [InlineData("mod-playerbots", false)]
    [InlineData(null, false)]
    public void IsAiChatModuleId_covers_every_module_driving_the_sidecar(string? moduleId, bool expected)
    {
        Assert.Equal(expected, OllamaSidecar.IsAiChatModuleId(moduleId));
    }

    [Fact]
    public void Default_rewrites_the_llm_chatter_base_url_off_the_docker_host()
    {
        var rule = OllamaSidecar.Default.ConfRewrites
            .Single(item => item.Key == LlmChatterBridge.OllamaBaseUrlKey);

        Assert.Equal(LlmChatterBridge.ConfFileName, rule.FileNameHint);
        Assert.Contains("http://host.docker.internal:11434", rule.LocalhostValues);
        Assert.Equal($"http://{OllamaSidecar.InternalHost}:{OllamaSidecar.InternalPort}", rule.SidecarValue);
    }

    [Fact]
    public void PlayerbotsChatterDisable_turns_off_built_in_talk_keys()
    {
        Assert.Equal(7, OllamaSidecar.PlayerbotsChatterDisable.Count);
        Assert.All(OllamaSidecar.PlayerbotsChatterDisable.Values, value => Assert.Equal("0", value));
        Assert.Contains("AiPlayerbot.EnableBroadcasts", OllamaSidecar.PlayerbotsChatterDisable.Keys);
        Assert.Contains("AiPlayerbot.RandomBotTalk", OllamaSidecar.PlayerbotsChatterDisable.Keys);
    }

    [Fact]
    public void OccupationTopicKeys_cover_the_behaviors_the_module_warns_about()
    {
        Assert.Equal(11, OllamaSidecar.OccupationTopicKeys.Count);
        Assert.Contains("OllamaChat.OccupationTopics.go_grind", OllamaSidecar.OccupationTopicKeys);
        Assert.Contains("OllamaChat.OccupationTopics.dummy", OllamaSidecar.OccupationTopicKeys);
        Assert.Contains("OllamaChat.OccupationTopics.loiter", OllamaSidecar.OccupationTopicKeys);
    }
}
