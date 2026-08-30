using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class ModuleSidecarConfTests
{
    [Fact]
    public void Apply_rewrites_localhost_defaults_and_leaves_custom_urls()
    {
        var etc = Path.Combine(Path.GetTempPath(), "azp-sidecar-conf-" + Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(etc, "modules");
        Directory.CreateDirectory(modules);
        File.WriteAllText(
            Path.Combine(modules, "mod_ollama_bot_buddy.conf"),
            "OllamaBotControl.Url = http://localhost:11434/api/generate\n");
        File.WriteAllText(
            Path.Combine(modules, "mod_ollama_chat.conf"),
            "OllamaChat.Url = http://localhost:11434/api/generate\n");

        try
        {
            ModuleSidecarConf.Apply(etc, [OllamaSidecar.Default]).Should().BeGreaterThanOrEqualTo(2);
            File.ReadAllText(Path.Combine(modules, "mod_ollama_bot_buddy.conf"))
                .Should()
                .Contain("http://ollama:11434/api/generate");
            File.ReadAllText(Path.Combine(modules, "mod_ollama_chat.conf"))
                .Should()
                .Contain("http://ollama:11434/api/generate")
                .And.NotContain("localhost")
                .And.Contain("OllamaChat.OccupationTopics.go_grind =");
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Fact]
    public void SeedAndApply_copies_dist_from_checkout_then_rewrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-sidecar-seed-" + Guid.NewGuid().ToString("N"));
        var etc = Path.Combine(root, "etc");
        var checkout = Path.Combine(root, "modules", "mod-ollama-chat", "conf");
        Directory.CreateDirectory(etc);
        Directory.CreateDirectory(checkout);
        File.WriteAllText(
            Path.Combine(checkout, "mod_ollama_chat.conf.dist"),
            "OllamaChat.Url = http://localhost:11434/api/generate\n");

        try
        {
            ModuleSidecarConf.SeedAndApply(etc, Path.Combine(root, "modules"), [OllamaSidecar.Default])
                .Should()
                .BeGreaterThan(0);
            var conf = Path.Combine(etc, "modules", "mod_ollama_chat.conf");
            File.Exists(conf).Should().BeTrue();
            File.ReadAllText(conf).Should().Contain("http://ollama:11434");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Apply_leaves_custom_chat_urls()
    {
        var etc = Path.Combine(Path.GetTempPath(), "azp-sidecar-custom-" + Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(etc, "modules");
        Directory.CreateDirectory(modules);
        File.WriteAllText(
            Path.Combine(modules, "mod_ollama_chat.conf"),
            "OllamaChat.Url = http://example.internal:11434/api/generate\n");

        try
        {
            ModuleSidecarConf.Apply(etc, [OllamaSidecar.Default]);
            File.ReadAllText(Path.Combine(modules, "mod_ollama_chat.conf"))
                .Should()
                .Contain("http://example.internal:11434/api/generate")
                .And.Contain("OllamaChat.OccupationTopics.dummy =");
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Fact]
    public void SeedFromCheckouts_copies_module_dist_without_sidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-sidecar-nosidecar-" + Guid.NewGuid().ToString("N"));
        var etc = Path.Combine(root, "etc");
        var checkout = Path.Combine(root, "modules", "mod-playerbots", "conf");
        Directory.CreateDirectory(etc);
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "playerbots.conf.dist"), "AiPlayerbot.Enabled = 1\n");

        try
        {
            ModuleSidecarConf.SeedFromCheckouts(etc, Path.Combine(root, "modules")).Should().Be(2);
            File.ReadAllText(Path.Combine(etc, "modules", "playerbots.conf.dist"))
                .Should().Be("AiPlayerbot.Enabled = 1\n");
            File.ReadAllText(Path.Combine(etc, "modules", "playerbots.conf"))
                .Should().Be("AiPlayerbot.Enabled = 1\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Apply_moves_llm_chatter_off_the_docker_host_alias()
    {
        var etc = Path.Combine(Path.GetTempPath(), "azp-sidecar-chatter-" + Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(etc, "modules");
        Directory.CreateDirectory(modules);
        var conf = Path.Combine(modules, LlmChatterBridge.ConfFileName);
        File.WriteAllText(conf, "LLMChatter.Ollama.BaseUrl = http://host.docker.internal:11434\n");

        try
        {
            ModuleSidecarConf.Apply(etc, [OllamaSidecar.Default]).Should().Be(1);
            File.ReadAllText(conf).Should().Contain("LLMChatter.Ollama.BaseUrl = http://ollama:11434");
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Fact]
    public void Apply_leaves_a_custom_llm_chatter_base_url()
    {
        var etc = Path.Combine(Path.GetTempPath(), "azp-sidecar-chatter-custom-" + Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(etc, "modules");
        Directory.CreateDirectory(modules);
        var conf = Path.Combine(modules, LlmChatterBridge.ConfFileName);
        File.WriteAllText(conf, "LLMChatter.Ollama.BaseUrl = http://gpu-box.lan:11434\n");

        try
        {
            ModuleSidecarConf.Apply(etc, [OllamaSidecar.Default]);
            File.ReadAllText(conf).Should().Contain("http://gpu-box.lan:11434");
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }

    [Fact]
    public void Apply_appends_missing_occupation_topic_keys_without_overwriting()
    {
        var etc = Path.Combine(Path.GetTempPath(), "azp-sidecar-topics-" + Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(etc, "modules");
        Directory.CreateDirectory(modules);
        File.WriteAllText(
            Path.Combine(modules, "mod_ollama_chat.conf"),
            "OllamaChat.Url = http://ollama:11434/api/generate\nOllamaChat.OccupationTopics.go_grind = keep-me\n");

        try
        {
            ModuleSidecarConf.Apply(etc, [OllamaSidecar.Default]);
            var content = File.ReadAllText(Path.Combine(modules, "mod_ollama_chat.conf"));
            content.Should().Contain("OllamaChat.OccupationTopics.go_grind = keep-me");
            content.Should().Contain("OllamaChat.OccupationTopics.loiter =");
            content.Should().Contain("OllamaChat.OccupationTopics.repair_sell =");
        }
        finally
        {
            Directory.Delete(etc, recursive: true);
        }
    }
}
