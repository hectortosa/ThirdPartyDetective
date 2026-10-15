using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThirdPartyDetective.Cli.Models;

namespace ThirdPartyDetective.Cli.Services;

public interface INoticeTemplateLoader
{
    NoticeTemplate Load(string? explicitPath);
}

public class NoticeTemplateLoader : INoticeTemplateLoader
{
    private const string DefaultFileName = ".tpd-notices.json";

    private static readonly JsonSerializerOptions JsonOptions = ConfigJson.CreateOptions();

    private readonly ILogger<NoticeTemplateLoader> _logger;

    public NoticeTemplateLoader(ILogger<NoticeTemplateLoader> logger)
    {
        _logger = logger;
    }

    public NoticeTemplate Load(string? explicitPath)
    {
        var path = explicitPath ?? DefaultFileName;
        if (!File.Exists(path))
        {
            if (explicitPath != null)
                throw new FileNotFoundException($"Notice template file not found: {explicitPath}", explicitPath);
            _logger.LogDebug("No {File} found in cwd; ATTRIBUTION.txt uses the default notes", DefaultFileName);
            return new NoticeTemplate();
        }

        _logger.LogInformation("Loading notice template from {Path}", path);
        try
        {
            var template = JsonSerializer.Deserialize<NoticeTemplate>(File.ReadAllText(path), JsonOptions) ?? new NoticeTemplate();
            // JSON deserializes into a case-sensitive dictionary; families match case-insensitively.
            template.CopyleftNotes = new Dictionary<string, string>(template.CopyleftNotes, StringComparer.OrdinalIgnoreCase);

            var unknown = template.CopyleftNotes.Keys
                .Where(k => !NoticeTemplate.CopyleftFamilies.Contains(k, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"Notice template '{path}': unknown copyleftNotes key(s) {string.Join(", ", unknown)}. Valid: {string.Join(", ", NoticeTemplate.CopyleftFamilies)}.");
            return template;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse notice template '{path}': {ex.Message}", ex);
        }
    }
}
