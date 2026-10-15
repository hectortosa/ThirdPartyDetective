namespace ThirdPartyDetective.Cli.Tests.Infrastructure;

// Golden-file assertions. Golden files record the exact output the tool
// produces today, so any behavior change shows up as a reviewable diff.
// Run `TPD_UPDATE_GOLDEN=1 dotnet test` to accept new output, then review
// the resulting git diff before committing.
internal static class GoldenFile
{
    public static void AssertMatches(string relativePath, string actual)
    {
        var path = Path.Combine(TestPaths.GoldenDir, relativePath);
        actual = NormalizeLineEndings(actual);

        // Golden files are shared: a machine-specific path in one is a scrubbing bug.
        Assert.DoesNotContain(TestPaths.RepoRoot, actual);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), actual);

        if (Environment.GetEnvironmentVariable("TPD_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path),
            $"Golden file '{relativePath}' is missing. Run with TPD_UPDATE_GOLDEN=1 to create it.");

        var expected = NormalizeLineEndings(File.ReadAllText(path));
        if (expected == actual) return;

        Assert.Fail($"Output differs from golden file '{relativePath}'. {DescribeFirstDifference(expected, actual)}\n" +
                    "If the change is intended, run with TPD_UPDATE_GOLDEN=1 and review the diff.");
    }

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");

    private static string DescribeFirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<end of file>";
            var al = i < a.Length ? a[i] : "<end of file>";
            if (el != al)
                return $"First difference at line {i + 1}:\n  expected: {el}\n  actual:   {al}";
        }
        return "Difference is in trailing content.";
    }
}
