using ThirdPartyDetective.Cli.Services.Registry;

namespace ThirdPartyDetective.Cli.Tests;

public class NuGetLicenseLookupTests
{
    [Theory]
    [InlineData("13.0.3", "13.0.3")]
    [InlineData("1.15.3-beta.1", "1.15.3-beta.1")]
    [InlineData("2.0.0-beta4.22272.1", "2.0.0-beta4.22272.1")]
    [InlineData("1.0.0+build.5", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.0.0.1", "1.0.0.1")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("01.002.3", "1.2.3")]
    [InlineData("1.0.0-RC.1", "1.0.0-rc.1")]
    public void Normalizes_versions_like_nuget(string version, string expected)
    {
        Assert.Equal(expected, NuGetLicenseLookup.NormalizeVersion(version));
    }
}
