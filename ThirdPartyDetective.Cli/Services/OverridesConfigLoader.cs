using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public interface IOverridesConfigLoader
{
    OverrideConfig Load(string? explicitPath);
}

public class OverridesConfigLoader : IOverridesConfigLoader
{
    private const string DefaultFileName = ".tpd-overrides.json";

    private static readonly JsonSerializerOptions JsonOptions = ConfigJson.CreateOptions();

    private readonly ILogger<OverridesConfigLoader> _logger;

    public OverridesConfigLoader(ILogger<OverridesConfigLoader> logger)
    {
        _logger = logger;
    }

    public OverrideConfig Load(string? explicitPath)
    {
        var path = explicitPath ?? DefaultFileName;
        if (!File.Exists(path))
        {
            if (explicitPath != null)
                throw new FileNotFoundException($"Overrides config file not found: {explicitPath}", explicitPath);
            _logger.LogDebug("No {File} found in cwd; no overrides will be applied", DefaultFileName);
            return new OverrideConfig();
        }

        _logger.LogInformation("Loading overrides config from {Path}", path);
        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<OverrideConfig>(json, JsonOptions)
                ?? new OverrideConfig();
            Validate(config, path);
            LoadLicenseTexts(config, path);
            _logger.LogInformation("Loaded {Count} override entries", config.Overrides.Count);
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse overrides config '{path}': {ex.Message}", ex);
        }
    }

    // License text files are resolved relative to the overrides file and must stay
    // inside its directory.
    private static void LoadLicenseTexts(OverrideConfig config, string path)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var entry in config.Overrides.Where(e => !string.IsNullOrWhiteSpace(e.LicenseTextFile)))
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, entry.LicenseTextFile!));
            if (!full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Overrides config '{path}': licenseTextFile '{entry.LicenseTextFile}' of '{entry.Name}@{entry.Version}' must be inside {baseDir}.");
            if (!File.Exists(full))
                throw new FileNotFoundException(
                    $"Overrides config '{path}': licenseTextFile '{entry.LicenseTextFile}' of '{entry.Name}@{entry.Version}' not found.", full);
            entry.LicenseText = File.ReadAllText(full);
        }
    }

    private static void Validate(OverrideConfig config, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Overrides.Count; i++)
        {
            var entry = config.Overrides[i];
            if (string.IsNullOrWhiteSpace(entry.Name))
                throw new InvalidOperationException($"Overrides config '{path}': entry {i} missing 'name'.");
            if (string.IsNullOrWhiteSpace(entry.Version))
                throw new InvalidOperationException($"Overrides config '{path}': entry {i} missing 'version'.");
            if (string.IsNullOrWhiteSpace(entry.License) &&
                string.IsNullOrWhiteSpace(entry.Authors) &&
                string.IsNullOrWhiteSpace(entry.Copyright) &&
                string.IsNullOrWhiteSpace(entry.LicenseTextFile))
                throw new InvalidOperationException(
                    $"Overrides config '{path}': entry '{entry.Name}@{entry.Version}' must specify at least one of license/authors/copyright/licenseTextFile.");
            // Package names match case-insensitively, so entries differing only in case collide.
            if (!seen.Add($"{entry.Name.ToLowerInvariant()}|{entry.Version}"))
                throw new InvalidOperationException(
                    $"Overrides config '{path}': duplicate entry for '{entry.Name}@{entry.Version}' (names match case-insensitively).");
        }
    }
}
