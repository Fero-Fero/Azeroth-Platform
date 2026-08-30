using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class LlmChatterConfTests
{
    private const string Dist = """
        LLMChatter.Enable = 1
        LLMChatter.Provider = anthropic
        LLMChatter.Model = claude-haiku-4-5-20251001
        LLMChatter.Ollama.BaseUrl = http://host.docker.internal:11434
        LLMChatter.Ollama.DisableThinking = 1
        LLMChatter.Database.Host = localhost
        LLMChatter.Database.Port = 3306
        LLMChatter.Database.User = root
        LLMChatter.Database.Password = acore
        LLMChatter.Database.Name = acore_characters
        """;

    [Fact]
    public void Apply_repoints_the_dist_at_the_stack_sidecar_and_database()
    {
        using var etc = new TempEtc(Dist);

        LlmChatterConf.Apply(etc.Path, "llama3.2:1b", "ac-database", 3306, "root", "s3cret", "acore_characters")
            .Should()
            .Be(1);

        var conf = etc.ReadConf();
        conf.Should().Contain("LLMChatter.Provider = ollama");
        conf.Should().Contain("LLMChatter.Model = llama3.2:1b");
        conf.Should().Contain("LLMChatter.Database.Host = ac-database");
        conf.Should().Contain("LLMChatter.Database.Password = s3cret");
    }

    [Fact]
    public void Apply_keeps_thinking_off()
    {
        using var etc = new TempEtc(Dist.Replace("LLMChatter.Ollama.DisableThinking = 1", string.Empty));

        LlmChatterConf.Apply(etc.Path, "llama3.2:1b", "ac-database", 3306, "root", "pw", "acore_characters");

        etc.ReadConf().Should().Contain("LLMChatter.Ollama.DisableThinking = 1");
    }

    [Fact]
    public void Apply_leaves_an_operators_own_provider_and_model_alone()
    {
        using var etc = new TempEtc("""
            LLMChatter.Provider = openai
            LLMChatter.Model = gpt-4o-mini
            LLMChatter.Database.Host = db.example.internal
            LLMChatter.Database.Password = chosen-by-operator
            """);

        LlmChatterConf.Apply(etc.Path, "llama3.2:1b", "ac-database", 3306, "root", "s3cret", "acore_characters");

        var conf = etc.ReadConf();
        conf.Should().Contain("LLMChatter.Provider = openai");
        conf.Should().Contain("LLMChatter.Model = gpt-4o-mini");
        conf.Should().Contain("LLMChatter.Database.Host = db.example.internal");
        conf.Should().Contain("LLMChatter.Database.Password = chosen-by-operator");
    }

    [Fact]
    public void Apply_ignores_other_module_confs()
    {
        using var etc = new TempEtc(Dist);
        var other = Path.Combine(etc.Path, "modules", "mod_ollama_chat.conf");
        File.WriteAllText(other, "LLMChatter.Provider = anthropic\n");

        LlmChatterConf.Apply(etc.Path, "llama3.2:1b", "ac-database", 3306, "root", "pw", "acore_characters");

        File.ReadAllText(other).Should().Contain("LLMChatter.Provider = anthropic");
    }

    private sealed class TempEtc : IDisposable
    {
        public TempEtc(string conf)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "azp-llm-chatter-" + Guid.NewGuid().ToString("N"));
            var modules = System.IO.Path.Combine(Path, "modules");
            Directory.CreateDirectory(modules);
            File.WriteAllText(System.IO.Path.Combine(modules, LlmChatterBridge.ConfFileName), conf);
        }

        public string Path { get; }

        public string ReadConf() =>
            File.ReadAllText(System.IO.Path.Combine(Path, "modules", LlmChatterBridge.ConfFileName));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
