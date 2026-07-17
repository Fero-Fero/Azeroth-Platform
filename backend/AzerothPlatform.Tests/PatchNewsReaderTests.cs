using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.IndividualProgression;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class PatchNewsReaderTests
{
    [Fact]
    public void TryReadArticle_parses_valid_article_json()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 1.1 Molten Core & Onyxia";
        var newsDir = Path.Combine(stack.Path, "migrations", patchKey, "news");
        Directory.CreateDirectory(newsDir);
        File.WriteAllText(
            Path.Combine(newsDir, "article.json"),
            """
            {
              "id": "progression-1-1-molten-core-onyxia",
              "title": "Fire and Shadow",
              "date": "2005-02-12",
              "tag": "patch",
              "isDraft": false,
              "html": "<p>The molten core bellows.</p>"
            }
            """);

        PatchNewsReader.TryReadArticle(stack.Path, patchKey, out var article, out _, out var error)
            .Should().BeTrue(error);

        article.Id.Should().Be("progression-1-1-molten-core-onyxia");
        article.Title.Should().Be("Fire and Shadow");
        article.Tag.Should().Be("patch");
        article.Html.Should().Contain("molten core bellows");
    }

    [Fact]
    public void TryReadArticle_rejects_draft_articles()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 1.0 Start";
        var newsDir = Path.Combine(stack.Path, "migrations", patchKey, "news");
        Directory.CreateDirectory(newsDir);
        File.WriteAllText(
            Path.Combine(newsDir, "article.json"),
            """
            {
              "id": "progression-1-0-start",
              "title": "Welcome",
              "isDraft": true,
              "html": "<p>Hi</p>"
            }
            """);

        PatchNewsReader.TryReadArticle(stack.Path, patchKey, out _, out _, out var error)
            .Should().BeFalse();

        error.Should().Contain("isDraft");
    }

    [Fact]
    public void TryReadArticle_loads_external_html_file()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 2.0 Karazhan";
        var newsDir = Path.Combine(stack.Path, "migrations", patchKey, "news");
        Directory.CreateDirectory(newsDir);
        File.WriteAllText(
            Path.Combine(newsDir, "article.json"),
            """
            {
              "id": "progression-2-0-tbc-entry",
              "title": "Burning Crusade Now Live",
              "date": "2007-01-16",
              "tag": "expansion",
              "htmlFile": "article.html"
            }
            """);
        File.WriteAllText(
            Path.Combine(newsDir, "article.html"),
            """
            <p>Prepare to tread through the Dark Portal.</p>
            <p><img src="images/karazhan.png" alt="Karazhan" /></p>
            """);

        PatchNewsReader.TryReadArticle(stack.Path, patchKey, out var article, out _, out var error)
            .Should().BeTrue(error);

        article.Html.Should().Contain("Dark Portal");
        article.Html.Should().Contain("images/karazhan.png");
    }

    [Fact]
    public void RewriteHtmlForPreview_rewrites_relative_image_paths()
    {
        const string html = """<img src="images/karazhan.png" alt="" />""";
        var rewritten = PatchNewsReader.RewriteHtmlForPreview(html, "stack-1", "patch 2.0 Karazhan");

        rewritten.Should().Contain("/api/stacks/stack-1/migrations/patch%202.0%20Karazhan/news-asset/images/karazhan.png");
    }

    [Fact]
    public void ToPublishedAssetId_encodes_relative_path()
    {
        PatchNewsReader.ToPublishedAssetId("progression-2-0", "images/karazhan.png")
            .Should().Be("progression-2-0--images-karazhan.png");
    }

    [Fact]
    public void ResolveCoverImagePath_finds_cover_png()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 1.0 Start";
        var newsDir = Path.Combine(stack.Path, "migrations", patchKey, "news");
        Directory.CreateDirectory(newsDir);
        File.WriteAllText(Path.Combine(newsDir, "cover.png"), "fake");

        PatchNewsReader.ResolveCoverImagePath(stack.Path, patchKey)
            .Should().EndWith("cover.png");
    }

    [Fact]
    public void SaveArticle_writes_json_and_html()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 1.1 Molten Core & Onyxia";
        PatchNewsWriter.SaveArticle(stack.Path, patchKey, new SavePatchNewsRequest
        {
            Id = "progression-1-1-molten-core-onyxia",
            Title = "Fire and Shadow",
            Date = "2005-02-12",
            Tag = "patch",
            Html = "<p>The molten core bellows.</p>",
        });

        PatchNewsReader.TryReadArticle(stack.Path, patchKey, out var article, out _, out var error)
            .Should().BeTrue(error);

        article.Title.Should().Be("Fire and Shadow");
        article.Html.Should().Contain("molten core bellows");
        File.Exists(Path.Combine(stack.Path, "migrations", patchKey, "news", "article.html")).Should().BeTrue();
    }

    [Fact]
    public void TryStampDate_updates_article_json_date()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 1.0 Start";
        PatchNewsWriter.SaveArticle(stack.Path, patchKey, new SavePatchNewsRequest
        {
            Id = "progression-1-0-start",
            Title = "Welcome",
            Date = "2004-11-23",
            Html = "<p>Hi</p>",
        });

        PatchNewsWriter.TryStampDate(stack.Path, patchKey, "2026-07-18", out var error)
            .Should().BeTrue(error);

        PatchNewsReader.TryReadArticle(stack.Path, patchKey, out var article, out _, out _)
            .Should().BeTrue();

        article.Date.Should().Be("2026-07-18");
    }

    [Fact]
    public void SaveArticle_honors_date_override()
    {
        using var stack = new TempDirectory();
        var patchKey = "patch 1.1 Molten Core & Onyxia";
        PatchNewsWriter.SaveArticle(
            stack.Path,
            patchKey,
            new SavePatchNewsRequest
            {
                Id = "progression-1-1-molten-core-onyxia",
                Title = "Fire and Shadow",
                Date = "2005-02-12",
                Html = "<p>Test</p>",
            },
            "2026-07-18");

        PatchNewsReader.TryReadArticle(stack.Path, patchKey, out var article, out _, out _)
            .Should().BeTrue();

        article.Date.Should().Be("2026-07-18");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
