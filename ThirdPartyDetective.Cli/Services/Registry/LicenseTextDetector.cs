using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ThirdPartyDetective.Cli.Services.Registry;

// A license identified from fetched text. FromRepositoryDefaultBranch is true
// when the text came from probing a repository's main/master branch, which
// may not match the license of the released version.
public record LicenseDetection(string LicenseId, string SourceUrl, bool FromRepositoryDefaultBranch);

public interface ILicenseTextDetector
{
    Task<LicenseDetection?> DetectFromUrlAsync(string url, CancellationToken cancellationToken = default);
    string? DetectFromContent(string content);
}

public class LicenseTextDetector : ILicenseTextDetector
{
    // Enough for the longest common license texts (GPL-3.0 is ~35 KB) while
    // keeping what an arbitrary URL can make us read bounded.
    private const int MaxBytesToRead = 64 * 1024;

    private const int MaxRedirects = 5;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    // License URLs come from third-party package metadata, so the tool only
    // follows them to public code hosts and license sites, never to arbitrary
    // hosts (internal services, cloud metadata endpoints) reachable from a CI
    // runner. Redirects are re-checked against the same list.
    internal static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "raw.githubusercontent.com", "gitlab.com", "bitbucket.org",
        "opensource.org", "www.opensource.org", "spdx.org", "www.spdx.org",
        "apache.org", "www.apache.org", "gnu.org", "www.gnu.org"
    };

    private readonly HttpClient _http;
    private readonly LicenseMatcher _matcher;
    private readonly ILogger<LicenseTextDetector> _logger;

    public LicenseTextDetector(HttpClient http, LicenseMatcher matcher, ILogger<LicenseTextDetector> logger)
    {
        _http = http;
        _matcher = matcher;
        _logger = logger;
    }

    public async Task<LicenseDetection?> DetectFromUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        // If the URL points to a GitHub repo (not a specific file), probe common LICENSE
        // filenames at common branches and use the first one that detects.
        var repoCoords = TryParseGitHubRepo(url);
        if (repoCoords != null)
        {
            var (owner, repo) = repoCoords.Value;
            return await ProbeGitHubLicenseAsync(owner, repo, cancellationToken);
        }

        var fetchUrl = NormalizeGitHubUrl(url);
        var detected = await FetchAndDetectAsync(fetchUrl, cancellationToken);
        return detected == null ? null : new LicenseDetection(detected, fetchUrl, FromRepositoryDefaultBranch: false);
    }

    public string? DetectFromContent(string content) => _matcher.Match(content);

    private static string NormalizeGitHubUrl(string url)
    {
        // github.com/user/repo/blob/branch/path → raw.githubusercontent.com/user/repo/branch/path
        if (url.Contains("github.com/") && url.Contains("/blob/") && !url.Contains("raw.githubusercontent.com"))
        {
            return url
                .Replace("github.com/", "raw.githubusercontent.com/")
                .Replace("/blob/", "/");
        }
        return url;
    }

    // Recognize GitHub repository URLs (where no specific file path is given). Handles:
    //   https://github.com/owner/repo
    //   https://github.com/owner/repo/
    //   git+https://github.com/owner/repo.git
    //   git://github.com/owner/repo.git
    private static readonly Regex GitHubRepoRegex = new(
        @"^(?:git\+)?https?://github\.com/([^/]+?)/([^/]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GitProtocolRepoRegex = new(
        @"^git://github\.com/([^/]+?)/([^/]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (string Owner, string Repo)? TryParseGitHubRepo(string url)
    {
        var match = GitHubRepoRegex.Match(url);
        if (!match.Success) match = GitProtocolRepoRegex.Match(url);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private async Task<LicenseDetection?> ProbeGitHubLicenseAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        // Try common LICENSE filename variants on the two common default branches.
        // Probed in parallel; first non-null wins (in candidate order).
        string[] branches = { "main", "master" };
        string[] files = { "LICENSE", "LICENSE.md", "LICENSE.txt", "license", "license.md", "license.txt" };

        var candidates = branches
            .SelectMany(b => files.Select(f => $"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/{b}/{f}"))
            .ToArray();

        var tasks = candidates.Select(c => FetchAndDetectAsync(c, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        for (int i = 0; i < results.Length; i++)
        {
            if (!string.IsNullOrEmpty(results[i]))
            {
                _logger.LogDebug("Detected license at {Url}: {Spdx}", candidates[i], results[i]);
                return new LicenseDetection(results[i]!, candidates[i], FromRepositoryDefaultBranch: true);
            }
        }
        return null;
    }

    private async Task<string?> FetchAndDetectAsync(string url, CancellationToken cancellationToken)
    {
        // Registry metadata carries all sorts of values here ("UNKNOWN", git+ssh://,
        // github:owner/repo); only absolute http(s) URLs on allowed hosts are fetched.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
        {
            _logger.LogDebug("Not fetching license URL outside the allowed hosts: {Url}", url);
            return null;
        }

        // HttpClient.Timeout stops at the response headers; this deadline also covers the body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FetchTimeout);
        var token = deadline.Token;

        try
        {
            for (int redirects = 0; ; redirects++)
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (redirects >= MaxRedirects || !IsAllowed(next))
                    {
                        _logger.LogDebug("Not following redirect from {Url} to {Next}", uri, next);
                        return null;
                    }
                    uri = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("License URL returned {Status}: {Url}", response.StatusCode, uri);
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[MaxBytesToRead];
                int totalRead = 0;
                while (totalRead < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), token);
                    if (read == 0) break;
                    totalRead += read;
                }

                return _matcher.Match(Encoding.UTF8.GetString(buffer, 0, totalRead));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One unreachable, slow or malformed URL must not abort the run.
            _logger.LogDebug(ex, "Failed to fetch license URL: {Url}", uri);
            return null;
        }
    }

    private static bool IsAllowed(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && AllowedHosts.Contains(uri.Host);
}
