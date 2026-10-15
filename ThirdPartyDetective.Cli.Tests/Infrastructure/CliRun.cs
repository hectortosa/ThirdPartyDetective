using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests.Infrastructure;

// Runs the real CLI in-process against an isolated temp directory: its own
// TPD_HOME, output directory and registry cache, with all HTTP routed to a
// FakeHttpHandler so nothing reaches the network. Appendix texts for listed
// SPDX IDs are short placeholders (see PlaceholderTextLicenseList) to keep
// golden files readable; license detection still matches the real bundled
// texts. TPD_HOME and the culture are process-wide, so tests using this belong
// to the ProcessEnvironment collection.
internal sealed class CliRun : IDisposable
{
    private static readonly Regex TimestampRegex = new(
        @"\d{4}-\d{2}-\d{2}[ T]\d{2}[:.]\d{2}[:.]\d{2}(\.\d+)?Z?", RegexOptions.Compiled);

    private readonly string? _originalTpdHome = Environment.GetEnvironmentVariable("TPD_HOME");
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public CliRun(FakeHttpHandler? http = null, CultureInfo? culture = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "tpd-cli-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Http = http ?? FakeHttpHandler.Empty();

        Environment.SetEnvironmentVariable("TPD_HOME", TpdHome);

        // Pin the culture so golden files don't depend on the machine running the tests.
        CultureInfo.CurrentCulture = culture ?? CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = culture ?? CultureInfo.InvariantCulture;
    }

    public string Root { get; }

    public string OutputDir => Path.Combine(Root, "out");

    public string TpdHome => Path.Combine(Root, "tpd-home");

    public string RegistryCachePath => Path.Combine(Root, "license-cache.json");

    public FakeHttpHandler Http { get; }

    // What the CLI writes to stdout / stderr during the run.
    public StringWriter Output { get; } = new();

    public StringWriter Error { get; } = new();

    // What the CLI logs during the run, instead of the console logger.
    public CapturingLoggerProvider Logs { get; } = new();

    // Writes license texts into TPD_HOME, where the tool looks for IDs that are
    // not in the bundled SPDX license list.
    public CliRun WithLicenseTexts(params string[] spdxIds)
    {
        var dir = Path.Combine(TpdHome, "licenses");
        Directory.CreateDirectory(dir);
        foreach (var id in spdxIds)
            File.WriteAllText(Path.Combine(dir, $"{id}.txt"), $"[{id} license text from TPD_HOME]\n");
        return this;
    }

    // Sets a process environment variable for this run; restored on Dispose.
    public CliRun WithEnvironment(string name, string? value)
    {
        _originalEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public async Task<int> RunAsync(params string[] args)
    {
        return await Program.RunAsync(args, new InvocationConfiguration { Output = Output, Error = Error }, services =>
        {
            services.AddLogging(logging => logging.ClearProviders().AddProvider(Logs));
            services.AddSingleton<ILicenseTextProvider>(sp => new LicenseTextProvider(
                new PlaceholderTextLicenseList(sp.GetRequiredService<ISpdxLicenseList>()),
                NullLogger<LicenseTextProvider>.Instance));
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = Http));
        });
    }

    public string ReadOutput(string fileName) =>
        Scrub(File.ReadAllText(Path.Combine(OutputDir, fileName)));

    // Replaces run-specific values (wall-clock timestamps, temp and checkout
    // paths) with stable placeholders.
    // Paths appear raw, and Markdown-escaped in packages.md.
    public string Scrub(string text) =>
        TimestampRegex.Replace(text, "<timestamp>")
            .Replace(Root, "<tmp>")
            .Replace(OutputText.MarkdownCell(Root), "<tmp>")
            .Replace(TestPaths.ProjectDir, "<tests>")
            .Replace(OutputText.MarkdownCell(TestPaths.ProjectDir), "<tests>");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TPD_HOME", _originalTpdHome);
        foreach (var (name, value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

// The bundled license list with every text replaced by a one-line placeholder.
internal sealed class PlaceholderTextLicenseList : ISpdxLicenseList
{
    private readonly ISpdxLicenseList _inner;

    public PlaceholderTextLicenseList(ISpdxLicenseList inner) => _inner = inner;

    public string Version => _inner.Version;

    public IReadOnlyList<string> LicenseIds => _inner.LicenseIds;

    public bool TryGetCanonicalId(string id, out string canonicalId) => _inner.TryGetCanonicalId(id, out canonicalId);

    public string? GetText(string canonicalId) =>
        _inner.GetText(canonicalId) == null ? null : $"[SPDX {_inner.Version} text of {canonicalId}]";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}
