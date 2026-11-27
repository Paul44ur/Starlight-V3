using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Starlight.Diagnostics;

/// <summary>
/// Sets up the logs that pile up across sessions for bulk tests:
/// starlight-*.log (everything, JSON lines) and errors-*.log (warnings and errors only).
/// </summary>
public static class Logging
{
    public static string Version { get; } =
        typeof(Logging).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static Logger Configure(StarlightPaths paths, LogEventLevel minimum = LogEventLevel.Information)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimum)
            .Enrich.WithProperty("Version", Version)
            .Enrich.FromLogContext()
            .Enrich.With<SecretMasker>()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.LogsDir, "starlight-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                fileSizeLimitBytes: 50_000_000, rollOnFileSizeLimit: true, shared: true)
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.LogsDir, "errors-.log"),
                restrictedToMinimumLevel: LogEventLevel.Warning,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                fileSizeLimitBytes: 20_000_000, rollOnFileSizeLimit: true, shared: true)
            .CreateLogger();
        Log.Logger = logger;
        return logger;
    }
}

/// <summary>Masks values of properties whose names look like secrets.</summary>
public sealed partial class SecretMasker : ILogEventEnricher
{
    private static readonly string[] Words = ["password", "token", "secret", "apikey", "api_key", "cookie"];

    /// <summary>Masks "password=..." style pairs and well-known API key shapes inside free text
    /// (tool arguments in the audit and trace, problem reports).</summary>
    public static string Mask(string text)
    {
        text = PairPattern().Replace(text, m => m.Groups[1].Value + "***");
        text = BearerPattern().Replace(text, m => m.Groups[1].Value + "***");
        text = JwtPattern().Replace(text, "***");
        text = PemPattern().Replace(text, "***");
        return KeyPattern().Replace(text, "***");
    }

    [System.Text.RegularExpressions.GeneratedRegex("""((?:password|passwd|pwd|token|secret|api[_-]?key|cookie)"?\s*[:=]\s*"?)[^\s",}]+""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PairPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abp]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})\b")]
    private static partial System.Text.RegularExpressions.Regex KeyPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"((?:Authorization\s*[:=]\s*)?(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BearerPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial System.Text.RegularExpressions.Regex JwtPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial System.Text.RegularExpressions.Regex PemPattern();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var key in logEvent.Properties.Keys.ToList())
        {
            if (Words.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, "***"));
            }
        }
    }
}
