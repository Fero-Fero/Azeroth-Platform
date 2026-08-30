using AzerothPlatform.Core.Modules;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Modules;

public sealed class ModuleMarkdownDependencyScannerTests
{
    [Fact]
    public void ScanText_maps_curl_and_nlohmann_and_ignores_known_non_apt_tokens()
    {
        const string markdown = """
            ## Dependencies Status
            - cURL / libcurl
            - nlohmann/json
            - fmtlib / {fmt}
            - cpp-httplib
            - OpenSSL
            - Ollama
            - Qdrant
            """;

        var packages = ModuleMarkdownDependencyScanner.ScanText(markdown);
        packages.Should().Equal(
            ModuleMarkdownDependencyScanner.LibCurlPackage,
            ModuleMarkdownDependencyScanner.NlohmannPackage);
    }

    [Fact]
    public void ScanText_skips_nlohmann_when_the_line_says_bundled()
    {
        const string markdown = """
            - nlohmann/json - bundled with module - no installation needed
            - cURL
            """;

        ModuleMarkdownDependencyScanner.ScanText(markdown)
            .Should()
            .Equal(ModuleMarkdownDependencyScanner.LibCurlPackage);
    }

    [Fact]
    public void ScanDirectory_reads_root_readme_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-md-scan-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(root, "README.md"), "- libcurl\n");
        File.WriteAllText(Path.Combine(src, "notes.md"), "- nlohmann/json\n");

        try
        {
            ModuleMarkdownDependencyScanner.ScanDirectory(root)
                .Should()
                .Equal(ModuleMarkdownDependencyScanner.LibCurlPackage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
