using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class NoticeTemplateLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly INoticeTemplateLoader _loader = new NoticeTemplateLoader(NullLogger<NoticeTemplateLoader>.Instance);

    public NoticeTemplateLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpd-notices-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string Write(string content)
    {
        var path = Path.Combine(_tempDir, ".tpd-notices.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Loads_notes_case_insensitively_with_comments()
    {
        var template = _loader.Load(Write("""
            {
              // reviewed by legal
              "header": "Source code: see below.",
              "copyleftNotes": { "lgpl": "LGPL note for {name}", "GPL": "" },
            }
            """));

        Assert.Equal("Source code: see below.", template.Header);
        Assert.Equal("LGPL note for {name}", template.CopyleftNotes["LGPL"]);
        Assert.Equal("", template.CopyleftNotes["gpl"]);
    }

    [Fact]
    public void Rejects_unknown_license_families()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _loader.Load(Write("""{ "copyleftNotes": { "MPL": "x" } }""")));
        Assert.Contains("unknown copyleftNotes key(s) MPL", ex.Message);
    }

    [Fact]
    public void Throws_when_explicit_path_does_not_exist()
    {
        Assert.Throws<FileNotFoundException>(() => _loader.Load(Path.Combine(_tempDir, "missing.json")));
    }
}
