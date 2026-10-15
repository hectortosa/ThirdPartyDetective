using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ThirdPartyDetective.Cli.Services;
using ThirdPartyDetective.Cli.Services.Registry;

namespace ThirdPartyDetective.Cli.Tests;

public class LicenseTextDetectorTests
{
    private static readonly LicenseMatcher Matcher = new(new BundledSpdxLicenseList());
    private static readonly string MitText = new BundledSpdxLicenseList().GetText("MIT")!;

    private static (LicenseTextDetector Detector, RoutingHandler Handler) Create(Func<Uri, HttpResponseMessage> respond)
    {
        var handler = new RoutingHandler(respond);
        return (new LicenseTextDetector(new HttpClient(handler), Matcher, NullLogger<LicenseTextDetector>.Instance), handler);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task Identifies_a_license_file_on_an_allowed_host()
    {
        var (detector, _) = Create(_ => Ok(MitText));

        var detection = await detector.DetectFromUrlAsync("https://raw.githubusercontent.com/owner/repo/v1.0/LICENSE");

        Assert.Equal(new LicenseDetection("MIT", "https://raw.githubusercontent.com/owner/repo/v1.0/LICENSE", false), detection);
    }

    [Theory]
    [InlineData("https://vendor.example/license.html")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost:8080/license")]
    [InlineData("git+ssh://git@github.com/o/r.git")]
    [InlineData("github:owner/repo")]
    [InlineData("UNKNOWN")]
    [InlineData("file:///etc/passwd")]
    public async Task Never_requests_urls_outside_the_allowed_hosts(string url)
    {
        var (detector, handler) = Create(_ => Ok(MitText));

        Assert.Null(await detector.DetectFromUrlAsync(url));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Follows_redirects_between_allowed_hosts()
    {
        var (detector, handler) = Create(uri => uri.Host == "github.com"
            ? Redirect("https://raw.githubusercontent.com/owner/repo/v1.0/LICENSE")
            : Ok(MitText));

        var detection = await detector.DetectFromUrlAsync("https://github.com/owner/repo/raw/v1.0/LICENSE");

        Assert.Equal("MIT", detection?.LicenseId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_follow_a_redirect_to_another_host()
    {
        var (detector, handler) = Create(_ => Redirect("http://169.254.169.254/latest/meta-data/"));

        Assert.Null(await detector.DetectFromUrlAsync("https://raw.githubusercontent.com/owner/repo/main/LICENSE"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Probing_a_repository_reports_the_default_branch()
    {
        var (detector, _) = Create(uri => uri.AbsolutePath == "/owner/repo/main/LICENSE"
            ? Ok(MitText)
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var detection = await detector.DetectFromUrlAsync("git+https://github.com/owner/repo.git");

        Assert.Equal(new LicenseDetection("MIT", "https://raw.githubusercontent.com/owner/repo/main/LICENSE", true), detection);
    }

    [Fact]
    public async Task A_failing_request_is_not_an_error()
    {
        var (detector, _) = Create(_ => throw new HttpRequestException("connection reset"));

        Assert.Null(await detector.DetectFromUrlAsync("https://raw.githubusercontent.com/owner/repo/main/LICENSE"));
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _respond;
        public RoutingHandler(Func<Uri, HttpResponseMessage> respond) => _respond = respond;
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(_respond(request.RequestUri!));
        }
    }
}
