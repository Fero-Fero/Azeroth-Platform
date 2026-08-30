using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Tests.TestSupport;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class AiChatSidecarNeedTests
{
    [Fact]
    public void Ollama_is_off_when_no_ai_module_is_selected()
    {
        using var etc = new TempDir();
        Assert.False(AiChatSidecarNeed.Ollama(etc.Path, ["mod-playerbots"]));
        Assert.False(AiChatSidecarNeed.LlmChatterBridgeContainer(etc.Path, ["mod-playerbots"]));
    }

    [Fact]
    public void Ollama_is_on_when_the_module_is_selected_and_etc_is_missing()
    {
        Assert.True(AiChatSidecarNeed.Ollama(null, [OllamaSidecar.ChatModuleId]));
        Assert.True(AiChatSidecarNeed.Ollama(@"D:\missing-etc", [OllamaSidecar.BuddyModuleId]));
        Assert.True(AiChatSidecarNeed.LlmChatterBridgeContainer(null, [LlmChatterBridge.ModuleId]));
    }

    [Theory]
    [InlineData(OllamaSidecar.ChatModuleId, OllamaSidecar.ChatConfFileName, OllamaSidecar.ChatEnableKey)]
    [InlineData(OllamaSidecar.BuddyModuleId, OllamaSidecar.BuddyConfFileName, OllamaSidecar.BuddyEnableKey)]
    [InlineData(LlmChatterBridge.ModuleId, LlmChatterBridge.ConfFileName, LlmChatterBridge.EnableKey)]
    public void Ollama_is_off_when_the_selected_module_enable_is_zero(
        string moduleId,
        string fileName,
        string key)
    {
        using var etc = EtcWith(fileName, $"{key} = 0\n");

        Assert.False(AiChatSidecarNeed.Ollama(etc.Path, [moduleId]));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void Ollama_is_on_when_chat_enable_is_set(string value)
    {
        using var etc = EtcWith(OllamaSidecar.ChatConfFileName, $"{OllamaSidecar.ChatEnableKey} = {value}\n");

        Assert.True(AiChatSidecarNeed.Ollama(etc.Path, [OllamaSidecar.ChatModuleId]));
    }

    [Fact]
    public void Ollama_is_on_when_the_enable_key_is_absent()
    {
        using var etc = EtcWith(OllamaSidecar.ChatConfFileName, "OllamaChat.Url = http://ollama:11434\n");

        Assert.True(AiChatSidecarNeed.Ollama(etc.Path, [OllamaSidecar.ChatModuleId]));
    }

    [Fact]
    public void Bridge_follows_llm_chatter_enable_only()
    {
        using var etc = EtcWith(LlmChatterBridge.ConfFileName, $"{LlmChatterBridge.EnableKey} = 0\n");

        Assert.False(AiChatSidecarNeed.LlmChatterBridgeContainer(etc.Path, [LlmChatterBridge.ModuleId]));
        Assert.False(AiChatSidecarNeed.Ollama(etc.Path, [LlmChatterBridge.ModuleId]));
    }

    [Fact]
    public void Bridge_stays_off_when_only_ollama_chat_is_selected()
    {
        using var etc = EtcWith(OllamaSidecar.ChatConfFileName, $"{OllamaSidecar.ChatEnableKey} = 1\n");

        Assert.True(AiChatSidecarNeed.Ollama(etc.Path, [OllamaSidecar.ChatModuleId]));
        Assert.False(AiChatSidecarNeed.LlmChatterBridgeContainer(etc.Path, [OllamaSidecar.ChatModuleId]));
    }

    private static TempDir EtcWith(string fileName, string content)
    {
        var etc = new TempDir("azp-ai-need");
        var modules = etc.Combine("modules");
        Directory.CreateDirectory(modules);
        File.WriteAllText(Path.Combine(modules, fileName), content);
        return etc;
    }
}
