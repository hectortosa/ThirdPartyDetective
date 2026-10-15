using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Services.Registry;

namespace ThirdPartyDetective.Cli.Tests;

public class RegistryFallbackEnricherTests : IDisposable
{
    private readonly string _tempDir;

    public RegistryFallbackEnricherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tpd-enricher-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<ThirdPartyInfo> Enrich(ThirdPartyInfo pkg, LicenseLookupResult? lookupResult, string? detected = null)
    {
        var enricher = new RegistryFallbackEnricher(
            new[] { new StubLookup(lookupResult) },
            new SpdxLicenseExpressionAdapter(),
            new StubDetector(detected),
            NullLogger<RegistryFallbackEnricher>.Instance);
        var options = new ScanOptions { RegistryFallbackCachePath = Path.Combine(_tempDir, "cache.json") };
        await enricher.EnrichAsync(new[] { pkg }, options);
        return pkg;
    }

    private static ThirdPartyInfo NuGetPackage(string? license = null) => new()
    {
        Name = "Some.Package",
        Version = "1.0.0",
        SourceEcosystem = "NuGet",
        LicenseExpression = license,
        LicenseSource = license == null ? LicenseSource.None : LicenseSource.Spdx
    };

    [Fact]
    public async Task Registry_license_never_replaces_an_SBOM_license()
    {
        var pkg = await Enrich(NuGetPackage(license: "MIT"),
            new LicenseLookupResult("GPL-3.0-or-later", null, Authors: "Someone", Copyright: "Copyright Someone"));

        Assert.Equal("MIT", pkg.LicenseExpression);
        Assert.Equal(LicenseSource.Spdx, pkg.LicenseSource);
        Assert.Equal("Copyright Someone", pkg.Copyright);
    }

    [Fact]
    public async Task Registry_license_fills_a_missing_license()
    {
        var pkg = await Enrich(NuGetPackage(), new LicenseLookupResult("Apache-2.0 WITH LLVM-exception", null));

        Assert.Equal("Apache-2.0 WITH LLVM-exception", pkg.LicenseExpression);
        Assert.Equal(new[] { "LLVM-exception" }, pkg.LicenseExceptions);
        Assert.Equal(LicenseSource.Registry, pkg.LicenseSource);
    }

    [Fact]
    public async Task Exact_license_url_is_used_before_text_detection()
    {
        var pkg = await Enrich(NuGetPackage(),
            new LicenseLookupResult(null, "http://www.apache.org/licenses/LICENSE-2.0"), detected: "MIT");

        Assert.Equal("Apache-2.0", pkg.LicenseExpression);
    }

    [Fact]
    public async Task Repository_url_is_a_project_url_not_a_license_url()
    {
        var pkg = await Enrich(NuGetPackage(),
            new LicenseLookupResult("MIT", null, ProjectUrl: "https://github.com/owner/repo"));

        Assert.Null(pkg.LicenseUrl);
        Assert.Equal("https://github.com/owner/repo", pkg.ProjectUrl);
    }

    [Fact]
    public async Task Detected_licenses_are_flagged_with_where_the_text_came_from()
    {
        var pkg = await Enrich(NuGetPackage(),
            new LicenseLookupResult(null, null, ProjectUrl: "https://github.com/owner/repo"), detected: "MIT");

        Assert.Equal("MIT", pkg.LicenseExpression);
        Assert.Contains(pkg.WarningFlags, w =>
            w.Contains("https://github.com/owner/repo (repository default branch, not necessarily this version)"));
    }

    [Fact]
    public async Task Package_license_text_is_kept_when_the_license_is_not_identified()
    {
        var pkg = await Enrich(NuGetPackage(), new LicenseLookupResult(null, null, LicenseText: "Custom terms"));

        Assert.Null(pkg.LicenseExpression);
        Assert.Equal("Custom terms", pkg.LicenseText);
    }

    [Fact]
    public async Task Copyright_comes_from_the_package_license_files_when_the_registry_has_none()
    {
        var pkg = await Enrich(NuGetPackage(),
            new LicenseLookupResult("MIT", null, LicenseText: "MIT License\n\nCopyright (c) 2020 Jane Doe\n\nPermission is hereby granted"));

        Assert.Equal("Copyright (c) 2020 Jane Doe", pkg.Copyright);
    }

    // MIT, BSD and ISC need the copyright notice; authors alone are not enough.
    [Fact]
    public async Task A_package_with_authors_but_no_copyright_is_still_enriched()
    {
        var pkg = NuGetPackage(license: "MIT");
        pkg.Authors = "Jane Doe";

        await Enrich(pkg, new LicenseLookupResult(null, null, Copyright: "Copyright (c) 2020 Jane Doe"));

        Assert.Equal("Copyright (c) 2020 Jane Doe", pkg.Copyright);
    }

    [Fact]
    public async Task A_registry_note_reaches_the_reviewer()
    {
        var pkg = await Enrich(NuGetPackage(), new LicenseLookupResult(null, null, Note: "npm declares the package UNLICENSED"));

        Assert.Contains("npm declares the package UNLICENSED", pkg.WarningFlags);
    }

    // A commercial package can live in a repository whose root LICENSE is MIT:
    // its own files, or a declaration that could not be mapped, outrank the repository.
    [Theory]
    [InlineData("Acme EULA", null)]
    [InlineData(null, "npm declares the license as 'Commercial', which names no SPDX license")]
    public async Task The_repository_is_not_probed_when_the_package_says_something_itself(string? licenseText, string? note)
    {
        var pkg = await Enrich(NuGetPackage(),
            new LicenseLookupResult(null, null, ProjectUrl: "https://github.com/owner/repo", LicenseText: licenseText, Note: note),
            detected: "MIT");

        Assert.Null(pkg.LicenseExpression);
    }

    [Fact]
    public async Task A_failing_lookup_does_not_abort_the_run()
    {
        var pkg = NuGetPackage();
        var enricher = new RegistryFallbackEnricher(
            new ILicenseLookup[] { new ThrowingLookup() },
            new SpdxLicenseExpressionAdapter(),
            new StubDetector(null),
            NullLogger<RegistryFallbackEnricher>.Instance);
        var cachePath = Path.Combine(_tempDir, "cache.json");

        await enricher.EnrichAsync(new[] { pkg }, new ScanOptions { RegistryFallbackCachePath = cachePath });

        Assert.Null(pkg.LicenseExpression);
        Assert.True(File.Exists(cachePath));
    }

    // Lookups finish in any order; a committed cache must only diff where entries changed.
    [Fact]
    public async Task The_cache_is_written_in_ordinal_key_order()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        File.WriteAllText(cachePath, $$"""
            { "version": {{LicenseCacheFile.CurrentVersion}}, "entries": {
              "nuget:alpha.package:1.0.0": { "license": "MIT", "resolved": "{{DateTime.UtcNow:O}}" },
              "nuget:Zeta.package:1.0.0": { "license": "MIT", "resolved": "{{DateTime.UtcNow:O}}" } } }
            """);

        await Enrich(NuGetPackage(), new LicenseLookupResult("MIT", null));

        var saved = File.ReadAllText(cachePath);
        var keys = new[] { "nuget:Zeta.package:1.0.0", "nuget:alpha.package:1.0.0", "nuget:some.package:1.0.0" };
        Assert.Equal(keys, keys.OrderBy(k => saved.IndexOf($"\"{k}\"", StringComparison.Ordinal)));
        Assert.All(keys, k => Assert.Contains($"\"{k}\"", saved));
    }

    [Fact]
    public async Task A_cached_entry_without_a_license_is_refetched_for_a_package_that_needs_one()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        File.WriteAllText(cachePath, $$"""
            { "version": {{LicenseCacheFile.CurrentVersion}}, "entries": {
              "nuget:some.package:1.0.0": { "authors": "Someone", "resolved": "{{DateTime.UtcNow:O}}" } } }
            """);

        var pkg = await Enrich(NuGetPackage(), new LicenseLookupResult("MIT", null));

        Assert.Equal("MIT", pkg.LicenseExpression);
    }

    [Fact]
    public async Task A_cached_entry_dated_in_the_future_is_not_trusted()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        File.WriteAllText(cachePath, $$"""
            { "version": {{LicenseCacheFile.CurrentVersion}}, "entries": {
              "nuget:some.package:1.0.0": { "license": "MIT", "resolved": "{{DateTime.UtcNow.AddYears(50):O}}" } } }
            """);

        var pkg = await Enrich(NuGetPackage(), new LicenseLookupResult("Apache-2.0", null));

        Assert.Equal("Apache-2.0", pkg.LicenseExpression);
    }

    [Fact]
    public async Task Offline_uses_cache_entries_of_any_age_and_leaves_the_file_alone()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        var content = $$"""
            { "version": {{LicenseCacheFile.CurrentVersion}}, "entries": {
              "nuget:some.package:1.0.0": { "license": "Apache-2.0", "resolved": "{{DateTime.UtcNow.AddDays(-400):O}}" } } }
            """;
        File.WriteAllText(cachePath, content);

        var pkg = NuGetPackage();
        var enricher = new RegistryFallbackEnricher(new[] { new StubLookup(new LicenseLookupResult("MIT", null)) },
            new SpdxLicenseExpressionAdapter(), new StubDetector(null), NullLogger<RegistryFallbackEnricher>.Instance);
        await enricher.EnrichAsync(new[] { pkg }, new ScanOptions { RegistryFallbackCachePath = cachePath, Offline = true });

        Assert.Equal("Apache-2.0", pkg.LicenseExpression);
        Assert.Equal(content, File.ReadAllText(cachePath));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_known_entry()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        File.WriteAllText(cachePath, $$"""
            { "version": {{LicenseCacheFile.CurrentVersion}}, "entries": {
              "nuget:some.package:1.0.0": { "license": "MIT", "copyright": "Copyright Someone", "resolved": "{{DateTime.UtcNow.AddDays(-60):O}}" } } }
            """);

        var pkg = await Enrich(NuGetPackage(), lookupResult: null);

        Assert.Equal("MIT", pkg.LicenseExpression);
        Assert.Contains("\"license\": \"MIT\"", File.ReadAllText(cachePath));
    }

    private sealed class ThrowingLookup : ILicenseLookup
    {
        public string Ecosystem => "nuget";
        public Task<LicenseLookupResult?> LookupLicenseAsync(string name, string version, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("registry returned something unexpected");
    }

    private sealed class StubLookup : ILicenseLookup
    {
        private readonly LicenseLookupResult? _result;
        public StubLookup(LicenseLookupResult? result) => _result = result;
        public string Ecosystem => "nuget";
        public Task<LicenseLookupResult?> LookupLicenseAsync(string name, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class StubDetector : ILicenseTextDetector
    {
        private readonly string? _detected;
        public StubDetector(string? detected) => _detected = detected;
        public Task<LicenseDetection?> DetectFromUrlAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(_detected == null ? null : new LicenseDetection(_detected, url, FromRepositoryDefaultBranch: url.Contains("github.com")));
        public string? DetectFromContent(string content) => _detected;
    }
}
