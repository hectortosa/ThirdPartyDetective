using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ThirdPartyDetective.Cli.Services;

// Log messages interpolate package names, versions, authors and URLs taken from
// SBOMs and registries. Writing them raw would let that metadata inject terminal
// escape sequences or line breaks into CI job logs (forging lines, or folding
// away the violations with GitLab section markers). Every message is cleaned
// with OutputText.Line and written as one line: "<level>: <message>".
public sealed class SanitizingConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "tpd-sanitizing";

    private const int MaxMessageLength = 16 * 1024;

    public SanitizingConsoleFormatter() : base(FormatterName)
    {
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception == null) return;

        textWriter.Write(LevelName(logEntry.LogLevel));
        textWriter.Write(": ");
        textWriter.Write(OutputText.Line(message, MaxMessageLength));
        if (logEntry.Exception is { } exception)
        {
            textWriter.Write(" | ");
            textWriter.Write(OutputText.Line($"{exception.GetType().Name}: {exception.Message}", MaxMessageLength));
        }
        textWriter.WriteLine();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => level.ToString()
    };
}
