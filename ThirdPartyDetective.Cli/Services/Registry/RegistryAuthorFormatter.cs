namespace ThirdPartyDetective.Cli.Services.Registry;

internal static class RegistryAuthorFormatter
{
    // Combine a name and email into the standard "Name <email>" form.
    // Returns null if both inputs are blank; returns whichever is non-blank
    // when only one is present.
    public static string? Combine(string? name, string? email)
    {
        var n = NormalizeBlank(name);
        var e = NormalizeBlank(email);
        if (n == null && e == null) return null;
        if (n == null) return e;
        if (e == null) return n;
        return $"{n} <{e}>";
    }

    private static string? NormalizeBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
