using System.Reflection;

namespace ThirdPartyDetective.Cli;

// Tool version as set by <Version> in Directory.Build.props. The SDK appends
// "+<commit sha>" to the informational version; strip it for log lines and
// the HTTP User-Agent (`--version` still prints the full value).
internal static class ToolVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(ToolVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return "0.0.0";

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
