using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using ThirdPartyDetective.Cli.Models;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Services.Registry;

namespace ThirdPartyDetective.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, new InvocationConfiguration());
    }

    // Parses and runs the command line. A command-line error exits 2 (operational
    // error) rather than System.CommandLine's default 1, which this tool reserves
    // for policy violations. configureServices runs after the default
    // registrations, so tests can swap services (e.g. the registries' HTTP handler)
    // without touching the CLI surface.
    internal static async Task<int> RunAsync(
        string[] args,
        InvocationConfiguration configuration,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        var parseResult = CreateRootCommand(configureServices).Parse(args);
        var exitCode = await parseResult.InvokeAsync(configuration, cancellationToken);
        return parseResult.Action is ParseErrorAction ? 2 : exitCode;
    }

    private static RootCommand CreateRootCommand(Action<IServiceCollection>? configureServices)
    {
        var sbomOption = new Option<string[]>("--sbom", "-s")
        {
            Description = "Path to an SPDX 2.2/2.3 JSON SBOM (Syft, Trivy, Microsoft.Sbom.Tool, etc.). Repeat to merge multiple sources; first non-NOASSERTION value wins per package.",
            Required = true
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for generated report files (not used for console format)",
            DefaultValueFactory = _ => "./licenses"
        };

        var formatOption = new Option<OutputFormat>("--format", "-f")
        {
            Description = "Output format",
            DefaultValueFactory = _ => OutputFormat.All
        };
        // Enum parsing also accepts numbers and combinations ("99", "Json,Html"), so the
        // token itself must be one of the format names.
        formatOption.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (!Enum.GetNames<OutputFormat>().Contains(token.Value, StringComparer.OrdinalIgnoreCase))
                    result.AddError($"Invalid --format '{token.Value}'. Valid: {string.Join(", ", Enum.GetNames<OutputFormat>().Select(n => n.ToLowerInvariant()))}.");
            }
        });

        var modeOption = new Option<string>("--mode", "-m")
        {
            Description = "Deployment mode for policy classification: saas, distribution (= byoc, on-premise), all",
            DefaultValueFactory = _ => "all"
        };

        var excludeLicenseTypesOption = new Option<string[]>("--exclude-license-types")
        {
            Description = "License types to treat as policy violations (additive on top of mode defaults)",
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        var includeLicenseTypesOption = new Option<string[]>("--include-license-types")
        {
            Description = "Strict whitelist: any package whose license type is NOT in this list is a violation. Replaces mode-default rules.",
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        var excludeNamespacesOption = new Option<string[]>("--exclude-namespaces")
        {
            Description = "Semicolon-separated list of package-name patterns to exclude entirely from output. Use trailing * for prefix match. Excluded packages don't appear in any report or count, don't trigger violations and are never sent to a registry. Recommended: --exclude-namespaces \"Microsoft.*;System.*\" to filter trusted Microsoft packages, plus your own first-party prefixes.",
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        var noFailOption = new Option<bool>("--no-fail")
        {
            Description = "Always exit 0 even when violations are found (default: exit 1 on violations)"
        };

        var registryFallbackOption = new Option<bool>("--registry-fallback")
        {
            Description = "For packages still NOASSERTION after merging all SBOMs, look up license info from the package registry (NuGet/npm/PyPI). Bounded I/O — only fires for the residual gap."
        };

        var registryFallbackCacheOption = new Option<string>("--registry-fallback-cache")
        {
            Description = "Path to the on-disk cache for registry-fallback license resolutions. If you commit it, review changes to it like code: an entry decides a package's license.",
            DefaultValueFactory = _ => "./.tpd-license-cache.json"
        };

        var policyOption = new Option<string?>("--policy")
        {
            Description = "Path to a JSON policy config defining per-mode license violations or whitelist. Auto-discovered as .tpd-policy.json in cwd if not specified."
        };

        var overridesOption = new Option<string?>("--overrides")
        {
            Description = "Path to a JSON overrides config recording manually verified (name, version) → license assignments. Auto-discovered as .tpd-overrides.json in cwd if not specified."
        };

        var noticeTemplateOption = new Option<string?>("--notice-template")
        {
            Description = "Path to a JSON notice template with legal wording for ATTRIBUTION.txt (copyleft notes, header, footer). Auto-discovered as .tpd-notices.json in cwd if not specified. Without one, notes state only facts."
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose logging"
        };

        var devInfoOption = new Option<bool>("--dev-info")
        {
            Description = "Include source ecosystem and project paths in output"
        };

        var includeLicenseTextOption = new Option<bool>("--include-license-text")
        {
            Description = "Append the text of every license, exception and LicenseRef the listed packages use to ATTRIBUTION.txt (Appendix A). Texts come from the SPDX license list bundled with the tool; nothing is downloaded."
        };

        var failOnUnresolvedOption = new Option<bool>("--fail-on-unresolved")
        {
            Description = "Exit 2 when a package's license could not be determined, or (with --include-license-text) a license text is missing from ATTRIBUTION.txt. Applies regardless of --no-fail."
        };

        var deterministicOption = new Option<bool>("--deterministic")
        {
            Description = "Omit the wall-clock timestamp from the reports, so identical inputs give byte-identical outputs. SOURCE_DATE_EPOCH, if set, is used as the timestamp instead."
        };

        var offlineOption = new Option<bool>("--offline")
        {
            Description = "Make no network requests: --registry-fallback only reads its cache. Also enabled by TPD_OFFLINE=1."
        };

        var rootCommand = new RootCommand("ThirdPartyDetective - Multi-source SPDX SBOM license-policy linter (SaaS / Distribution)")
        {
            sbomOption,
            outputOption,
            formatOption,
            modeOption,
            excludeLicenseTypesOption,
            includeLicenseTypesOption,
            excludeNamespacesOption,
            noFailOption,
            registryFallbackOption,
            registryFallbackCacheOption,
            policyOption,
            overridesOption,
            noticeTemplateOption,
            includeLicenseTextOption,
            failOnUnresolvedOption,
            deterministicOption,
            offlineOption,
            verboseOption,
            devInfoOption
        };

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var error = parseResult.InvocationConfiguration.Error;

            var rawMode = parseResult.GetValue(modeOption) ?? "all";
            ScanMode mode;
            try
            {
                mode = ParseMode(rawMode);
            }
            catch (ArgumentException ex)
            {
                error.WriteLine($"Error: {ex.Message}");
                return 2;
            }

            var sbomPaths = (parseResult.GetValue(sbomOption) ?? Array.Empty<string>()).ToList();
            if (sbomPaths.Count == 0)
            {
                error.WriteLine("Error: at least one --sbom path is required.");
                return 2;
            }

            var options = new ScanOptions
            {
                SbomPaths = sbomPaths,
                Output = parseResult.GetValue(outputOption)!,
                Format = parseResult.GetValue(formatOption),
                Mode = mode,
                ExcludeLicenseTypes = parseResult.GetValue(excludeLicenseTypesOption)!.ToList(),
                IncludeLicenseTypes = parseResult.GetValue(includeLicenseTypesOption)!.ToList(),
                ExcludeNamespaces = (parseResult.GetValue(excludeNamespacesOption) ?? Array.Empty<string>())
                    .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList(),
                NoFail = parseResult.GetValue(noFailOption),
                RegistryFallback = parseResult.GetValue(registryFallbackOption),
                RegistryFallbackCachePath = parseResult.GetValue(registryFallbackCacheOption)!,
                PolicyConfigPath = parseResult.GetValue(policyOption),
                OverridesConfigPath = parseResult.GetValue(overridesOption),
                NoticeTemplatePath = parseResult.GetValue(noticeTemplateOption),
                Verbose = parseResult.GetValue(verboseOption),
                DevInfo = parseResult.GetValue(devInfoOption),
                IncludeLicenseText = parseResult.GetValue(includeLicenseTextOption),
                FailOnUnresolved = parseResult.GetValue(failOnUnresolvedOption),
                Deterministic = parseResult.GetValue(deterministicOption),
                SourceDateEpoch = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"),
                Offline = parseResult.GetValue(offlineOption) ||
                          Environment.GetEnvironmentVariable("TPD_OFFLINE") == "1"
            };

            try
            {
                var result = await RunClassificationAsync(options, configureServices, cancellationToken);
                var unresolved = result.Summary.UnresolvedLicenses.Count + result.Summary.MissingLicenseTexts.Count;
                if (options.FailOnUnresolved && unresolved > 0)
                {
                    error.WriteLine(
                        $"Error: {result.Summary.UnresolvedLicenses.Count} package(s) with undetermined license and " +
                        $"{result.Summary.MissingLicenseTexts.Count} missing license text(s) (--fail-on-unresolved).");
                    return 2;
                }
                return (result.Violations.Count > 0 && !options.NoFail) ? 1 : 0;
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or ArgumentException)
            {
                error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                error.WriteLine($"Unexpected error: {ex.Message}");
                if (options.Verbose)
                    error.WriteLine(ex.StackTrace);
                return 2;
            }
        });

        return rootCommand;
    }

    private static ScanMode ParseMode(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "saas" => ScanMode.Saas,
        "distribution" or "byoc" or "on-premise" or "onpremise" => ScanMode.Distribution,
        "all" or "" => ScanMode.All,
        _ => throw new ArgumentException($"Unknown mode '{raw}'. Valid: saas, distribution, byoc, on-premise, all.")
    };

    private static async Task<PackageAnalysisResult> RunClassificationAsync(
        ScanOptions options,
        Action<IServiceCollection>? configureServices,
        CancellationToken cancellationToken)
    {
        var host = CreateHost(options, configureServices);
        await host.StartAsync(cancellationToken);

        try
        {
            var classifier = host.Services.GetRequiredService<ISbomClassifier>();
            var outputGenerator = host.Services.GetRequiredService<IOutputGenerator>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            logger.LogInformation("ThirdPartyDetective {Version} - SBOM classifier", ToolVersion.Current);
            logger.LogInformation("SBOM sources ({Count}): {Sboms}",
                options.SbomPaths.Count, string.Join(", ", options.SbomPaths));
            logger.LogInformation("Mode: {Mode}", options.Mode);
            if (options.RegistryFallback)
                logger.LogInformation("Registry fallback enabled{Offline}, cache: {Cache}",
                    options.Offline ? " (offline: cache only)" : "", options.RegistryFallbackCachePath);

            var result = await classifier.ClassifyAsync(options, cancellationToken);
            await outputGenerator.GenerateOutputAsync(result, options, cancellationToken);

            logger.LogInformation(
                "Classification complete: {Total} packages, {Violations} violations",
                result.Summary.TotalPackages, result.Violations.Count);

            if (result.Summary.WarningCount > 0)
                logger.LogWarning("{WarningCount} warnings require attention", result.Summary.WarningCount);
            if (result.Summary.UnresolvedLicenses.Count > 0)
                logger.LogWarning("License not determined for {Count} package(s): {Packages}. Add entries to {OverridesFile}.",
                    result.Summary.UnresolvedLicenses.Count, string.Join(", ", result.Summary.UnresolvedLicenses),
                    options.OverridesConfigPath ?? ".tpd-overrides.json");
            if (result.Summary.MissingLicenseTexts.Count > 0)
                logger.LogWarning("No license text available for: {Licenses}",
                    string.Join(", ", result.Summary.MissingLicenseTexts));

            return result;
        }
        finally
        {
            await host.StopAsync(cancellationToken);
        }
    }

    private static IHost CreateHost(ScanOptions options, Action<IServiceCollection>? configureServices)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(console => console.FormatterName = SanitizingConsoleFormatter.FormatterName);
                logging.AddConsoleFormatter<SanitizingConsoleFormatter, ConsoleFormatterOptions>();
                logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
                // HttpClientFactory logs four lines per request; a registry run makes
                // hundreds of requests. They are kept for --verbose.
                logging.AddFilter("System.Net.Http.HttpClient", options.Verbose ? LogLevel.Debug : LogLevel.Warning);
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISpdxLicenseExpressionAdapter, SpdxLicenseExpressionAdapter>();
                services.AddSingleton<ISpdxSbomReader, SpdxSbomReader>();
                services.AddSingleton<ISbomMerger, SbomMerger>();
                services.AddSingleton<IRegistryFallbackEnricher, RegistryFallbackEnricher>();
                services.AddSingleton<IPolicyConfigLoader, PolicyConfigLoader>();
                services.AddSingleton<IOverridesConfigLoader, OverridesConfigLoader>();
                services.AddSingleton<IOverrideApplier, OverrideApplier>();
                services.AddSingleton<INoticeTemplateLoader, NoticeTemplateLoader>();
                services.AddSingleton<ISbomClassifier, SbomClassifier>();
                services.AddSingleton<ILicenseAnalyzer, LicenseAnalyzer>();
                services.AddSingleton<IOutputGenerator, OutputGenerator>();

                services.AddHttpClient<NuGetLicenseLookup>(ConfigureRegistryClient)
                    .ConfigurePrimaryHttpMessageHandler(CreateDecompressingHandler);
                services.AddHttpClient<NpmLicenseLookup>(ConfigureRegistryClient)
                    .ConfigurePrimaryHttpMessageHandler(CreateDecompressingHandler);
                services.AddHttpClient<PyPiLicenseLookup>(ConfigureRegistryClient)
                    .ConfigurePrimaryHttpMessageHandler(CreateDecompressingHandler);
                // Redirects are followed by LicenseTextDetector itself, so each hop is checked.
                services.AddHttpClient<LicenseTextDetector>(ConfigureRegistryClient)
                    .ConfigurePrimaryHttpMessageHandler(() => CreateDecompressingHandler(allowAutoRedirect: false));

                services.AddSingleton<ILicenseLookup>(sp => sp.GetRequiredService<NuGetLicenseLookup>());
                services.AddSingleton<ILicenseLookup>(sp => sp.GetRequiredService<NpmLicenseLookup>());
                services.AddSingleton<ILicenseLookup>(sp => sp.GetRequiredService<PyPiLicenseLookup>());
                services.AddSingleton<ILicenseTextDetector>(sp => sp.GetRequiredService<LicenseTextDetector>());
                services.AddSingleton<ISpdxLicenseList, BundledSpdxLicenseList>();
                services.AddSingleton<LicenseMatcher>();
                services.AddSingleton<ILicenseTextProvider, LicenseTextProvider>();

                configureServices?.Invoke(services);
            })
            .Build();
    }

    private static void ConfigureRegistryClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("User-Agent", $"ThirdPartyDetective/{ToolVersion.Current}");
    }

    private static HttpClientHandler CreateDecompressingHandler() => CreateDecompressingHandler(allowAutoRedirect: true);

    private static HttpClientHandler CreateDecompressingHandler(bool allowAutoRedirect) => new()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
        AllowAutoRedirect = allowAutoRedirect,
        MaxAutomaticRedirections = 5
    };
}
