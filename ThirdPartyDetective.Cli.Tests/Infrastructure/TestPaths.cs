namespace ThirdPartyDetective.Cli.Tests.Infrastructure;

// Fixtures are read from, and golden files written to, the source tree rather
// than the build output, so `TPD_UPDATE_GOLDEN=1 dotnet test` rewrites the
// checked-in files directly. The project directory is found by walking up from
// the test assembly, which keeps working when CI maps source paths.
internal static class TestPaths
{
    public static string ProjectDir { get; } = FindProjectDir();

    public static string RepoRoot => Path.GetDirectoryName(ProjectDir)!;

    public static string FixturesDir => Path.Combine(ProjectDir, "Fixtures");

    public static string GoldenDir => Path.Combine(ProjectDir, "Golden");

    public static string Fixture(params string[] parts) =>
        Path.Combine(new[] { FixturesDir }.Concat(parts).ToArray());

    private static string FindProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ThirdPartyDetective.Cli.Tests.csproj")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not find ThirdPartyDetective.Cli.Tests.csproj above {AppContext.BaseDirectory}.");
    }
}
