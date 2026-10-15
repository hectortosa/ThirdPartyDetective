using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public interface IPolicyConfigLoader
{
    PolicyConfig Load(string? explicitPath);
}

public class PolicyConfigLoader : IPolicyConfigLoader
{
    private const string DefaultFileName = ".tpd-policy.json";

    private static readonly JsonSerializerOptions JsonOptions = BuildJsonOptions();

    private static JsonSerializerOptions BuildJsonOptions()
    {
        var opts = ConfigJson.CreateOptions();
        opts.Converters.Add(new SpdxOrNameLicenseTypeConverter());
        return opts;
    }

    private readonly ILogger<PolicyConfigLoader> _logger;

    public PolicyConfigLoader(ILogger<PolicyConfigLoader> logger)
    {
        _logger = logger;
    }

    public PolicyConfig Load(string? explicitPath)
    {
        var path = explicitPath ?? DefaultFileName;
        if (!File.Exists(path))
        {
            if (explicitPath != null)
                throw new FileNotFoundException($"Policy config file not found: {explicitPath}", explicitPath);
            _logger.LogDebug("No {File} found in cwd; using built-in default policy", DefaultFileName);
            return PolicyConfig.Default();
        }

        _logger.LogInformation("Loading policy config from {Path}", path);
        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<PolicyConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException("Policy config file is empty or invalid.");
            // JSON deserializes into a case-sensitive dictionary; mode keys ("saas", "distribution")
            // need case-insensitive lookup since ScanMode.ToString() is "Saas"/"Distribution".
            config.Modes = new Dictionary<string, ModePolicy>(config.Modes, StringComparer.OrdinalIgnoreCase);
            Validate(config, path);
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse policy config '{path}': {ex.Message}", ex);
        }
    }

    private static void Validate(PolicyConfig config, string path)
    {
        foreach (var (modeName, policy) in config.Modes)
        {
            if (policy.Violations != null && policy.Allowed != null)
                throw new InvalidOperationException(
                    $"Policy config '{path}' mode '{modeName}': cannot specify both 'violations' and 'allowed' (they are mutually exclusive).");
            if (policy.Violations == null && policy.Allowed == null)
                throw new InvalidOperationException(
                    $"Policy config '{path}' mode '{modeName}': must specify either 'violations' or 'allowed'.");
        }
    }
}

// Accepts SPDX IDs (MIT, Apache-2.0, AGPL-3.0) AND LicenseType enum names (Apache20, Proprietary, Unknown, Multiple).
internal class SpdxOrNameLicenseTypeConverter : JsonConverter<LicenseType>
{
    public override LicenseType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string for license identifier, got {reader.TokenType}.");
        var value = reader.GetString() ?? throw new JsonException("License identifier was null.");

        if (Enum.TryParse<LicenseType>(value, ignoreCase: true, out var enumType))
            return enumType;

        if (LicenseTypeRegistry.TryMap(value, out var spdxType))
            return spdxType;

        throw new JsonException(
            $"Unknown license identifier '{value}'. Use an SPDX ID (e.g. \"Apache-2.0\") or a LicenseType name (e.g. \"Proprietary\", \"Unknown\", \"Multiple\").");
    }

    public override void Write(Utf8JsonWriter writer, LicenseType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
