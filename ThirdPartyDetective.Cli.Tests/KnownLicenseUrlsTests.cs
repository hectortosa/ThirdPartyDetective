using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class KnownLicenseUrlsTests
{
    [Theory]
    [InlineData("https://licenses.nuget.org/MIT", "MIT")]
    [InlineData("https://licenses.nuget.org/PostgreSQL", "PostgreSQL")]
    [InlineData("https://licenses.nuget.org/(MIT%20OR%20Apache-2.0)", "MIT OR Apache-2.0")]
    [InlineData("https://licenses.nuget.org/EPL-1.0", "EPL-1.0")]
    [InlineData("https://spdx.org/licenses/BSD-3-Clause.html", "BSD-3-Clause")]
    [InlineData("https://opensource.org/licenses/MIT", "MIT")]
    [InlineData("http://www.opensource.org/licenses/mit-license.php", "MIT")]
    [InlineData("http://www.apache.org/licenses/LICENSE-2.0", "Apache-2.0")]
    [InlineData("https://www.apache.org/licenses/LICENSE-2.0.txt", "Apache-2.0")]
    [InlineData("https://opensource.org/licenses/mit", "MIT")]
    public void Maps_urls_that_name_the_license_exactly(string url, string expected)
    {
        Assert.True(KnownLicenseUrls.TryGetExpression(url, out var expression));
        Assert.Equal(expected, expression);
    }

    [Theory]
    [InlineData("https://github.com/jsmith/proprietary-sdk/blob/main/EULA.txt")]
    [InlineData("https://github.com/mitsuhiko/flask")]
    [InlineData("https://vendor.example/commercial-license")]
    [InlineData("https://www.gnu.org/licenses/agpl-3.0.html")]
    [InlineData("https://aka.ms/deprecateLicenseUrl")]
    [InlineData("git+ssh://git@github.com/o/r.git")]
    [InlineData("UNKNOWN")]
    [InlineData("http://www.opensource.org/licenses/bsd-license.php")]
    [InlineData("https://spdx.org/licenses/index.html")]
    [InlineData("https://licenses.nuget.org/LICENSE.txt")]
    [InlineData(null)]
    public void Does_not_guess_from_other_urls(string? url)
    {
        Assert.False(KnownLicenseUrls.TryGetExpression(url, out _));
    }
}
