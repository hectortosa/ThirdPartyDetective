namespace ThirdPartyDetective.Cli.Services;

// Standard storage location for tool-managed assets (license corpus, future caches).
// Pattern matches gh, aws, terraform: ~/<dot-dirname>. Override the root via the
// TPD_HOME environment variable for sandboxed CI or per-user customization.
public static class LicensePaths
{
    private const string DotDirName = ".thirdpartydetective";

    public static string Root
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("TPD_HOME");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, DotDirName);
        }
    }

    public static string LicensesDir => Path.Combine(Root, "licenses");

    public static string LicenseFile(string spdxId) =>
        Path.Combine(LicensesDir, $"{spdxId}.txt");
}
