using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Tests.Infrastructure;

namespace ThirdPartyDetective.Cli.Tests;

// Characterizes how registry data is turned into a license: LicenseMatcher
// (license file contents, via LicenseTextDetector) and LicenseAnalyzer's URL
// handling. The golden tables show what each input is classified as.
public class LicenseDetectionGoldenTests
{
    // LicenseTextDetector reads at most this many bytes from a license URL.
    private const int UrlReadLimitBytes = 64 * 1024;

    private static readonly LicenseMatcher Matcher = new(new BundledSpdxLicenseList());

    [Fact]
    public void Canonical_spdx_texts_as_seen_by_LicenseTextDetector()
    {
        var textsDir = TestPaths.Fixture("spdx-texts");
        var version = File.ReadAllText(Path.Combine(textsDir, "VERSION")).Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"# Canonical SPDX texts (license-list-data {version}) -> LicenseMatcher result");
        sb.AppendLine("# full: whole file (.nupkg licenseFile path); url: first 64 KB (license URL path)");

        foreach (var path in Directory.GetFiles(textsDir, "*.txt").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            var full = Matcher.Match(Encoding.UTF8.GetString(bytes));
            var url = Matcher.Match(Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, UrlReadLimitBytes)));
            sb.AppendLine($"{Path.GetFileNameWithoutExtension(path),-16} full: {full ?? "(none)",-12} url: {url ?? "(none)"}");
        }

        GoldenFile.AssertMatches("detection/license-text-detector.txt", sb.ToString());
    }

    // License files as projects actually write them: filled-in copyright lines,
    // extra headers, notices instead of license texts, combined licenses.
    [Fact]
    public void Real_world_license_files_as_seen_by_LicenseMatcher()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Real-world style license files -> LicenseMatcher result");
        foreach (var path in Directory.GetFiles(TestPaths.Fixture("license-files"), "*.txt").Order(StringComparer.Ordinal))
            sb.AppendLine($"{Path.GetFileName(path),-34} {Matcher.Match(File.ReadAllText(path)) ?? "(none)"}");

        GoldenFile.AssertMatches("detection/real-world-license-files.txt", sb.ToString());
    }

    [Fact]
    public void License_urls_as_seen_by_LicenseAnalyzer()
    {
        string[] urls =
        {
            "https://licenses.nuget.org/MIT",
            "https://licenses.nuget.org/Apache-2.0",
            "https://licenses.nuget.org/EPL-1.0",
            "https://licenses.nuget.org/MPL-1.1",
            "https://licenses.nuget.org/LGPL-2.0-or-later",
            "https://licenses.nuget.org/BSD-4-Clause",
            "https://www.gnu.org/licenses/lgpl-2.1.html",
            "https://www.gnu.org/licenses/agpl-3.0.html",
            "http://logging.apache.org/log4net/license.html",
            "http://www.antlr3.org/license.html",
            "https://github.com/jsmith/proprietary-sdk/blob/main/EULA.txt",
            "https://github.com/mitsuhiko/flask",
            "https://vendor.example/commercial-license",
            "https://example.invalid/terms"
        };

        var analyzer = new LicenseAnalyzer(NullLogger<LicenseAnalyzer>.Instance);
        var sb = new StringBuilder();
        sb.AppendLine("# License URL (no expression) -> LicenseType from LicenseAnalyzer.ParseLicenseType");
        foreach (var url in urls)
            sb.AppendLine($"{url,-66} {analyzer.ParseLicenseType(null, url)}");

        GoldenFile.AssertMatches("detection/license-url-heuristic.txt", sb.ToString());
    }
}
