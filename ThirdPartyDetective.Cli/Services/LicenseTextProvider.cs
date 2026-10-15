using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ThirdPartyDetective.Cli.Services;

public interface ILicenseTextProvider
{
    // Returns the canonical SPDX text for a license or exception ID as it appears
    // in an expression ("MIT", "GPL-2.0+", "Classpath-exception-2.0"), or null if
    // unavailable. LicenseRef-* IDs have no canonical text and always return null.
    // Texts come from the bundled SPDX license list; for IDs it doesn't know (a
    // newer list version), a text placed at $TPD_HOME/licenses/<id>.txt is used.
    // Nothing is fetched at runtime.
    Task<string?> GetTextAsync(string licenseId, CancellationToken cancellationToken = default);
}

public class LicenseTextProvider : ILicenseTextProvider
{
    // SPDX idstring characters only; anything else (LicenseRef-x:y, path
    // separators, "..") never reaches the file system or a URL.
    private static readonly Regex SpdxIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,99}$", RegexOptions.Compiled);

    // GNU licenses whose bare and "+" forms are deprecated in SPDX 3; the list
    // publishes their texts only as "-only" / "-or-later".
    private static readonly HashSet<string> GnuFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        "GPL-1.0", "GPL-2.0", "GPL-3.0", "LGPL-2.0", "LGPL-2.1", "LGPL-3.0",
        "AGPL-1.0", "AGPL-3.0", "GFDL-1.1", "GFDL-1.2", "GFDL-1.3"
    };

    private readonly ISpdxLicenseList _licenseList;
    private readonly ILogger<LicenseTextProvider> _logger;

    public LicenseTextProvider(ISpdxLicenseList licenseList, ILogger<LicenseTextProvider> logger)
    {
        _licenseList = licenseList;
        _logger = logger;
    }

    // The license-list-data file that holds the text for an ID as written in an
    // expression: "GPL-2.0+" → "GPL-2.0-or-later", "GPL-3.0" → "GPL-3.0-only".
    // Returns null for IDs that cannot name an SPDX text file.
    public static string? TextFileId(string licenseId)
    {
        var orLater = licenseId.EndsWith('+');
        var id = orLater ? licenseId[..^1] : licenseId;
        if (!SpdxIdRegex.IsMatch(id) || id.StartsWith("LicenseRef-", StringComparison.OrdinalIgnoreCase))
            return null;

        if (GnuFamily.Contains(id))
            return id + (orLater ? "-or-later" : "-only");
        return id;
    }

    public async Task<string?> GetTextAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        var fileId = TextFileId(licenseId);
        if (fileId == null) return null;

        if (_licenseList.TryGetCanonicalId(fileId, out var canonicalId) && _licenseList.GetText(canonicalId) is { } text)
            return text;

        var path = LicensePaths.LicenseFile(fileId);
        if (!File.Exists(path)) return null;

        try
        {
            _logger.LogInformation("Using license text for {Spdx} from {Path} (not in SPDX license list {Version})",
                fileId, path, _licenseList.Version);
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read license text for {Spdx} at {Path}", fileId, path);
            return null;
        }
    }
}
