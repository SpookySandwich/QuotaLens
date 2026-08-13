namespace QuotaLens.Core;

/// <summary>
/// Minimal append-only file log for diagnosing provider/login issues, e.g. why a
/// provider still reports "Login required" after a completed CLI sign-in.
///
/// The log file is TRUNCATED on every app start so it can never grow unbounded:
/// it always holds exactly the most recent session. Written under
/// %LOCALAPPDATA%QuotaLenslogsquotalens.log (shared read access so the file
/// can be opened/tailed while the app is running).
///
/// Every call is best-effort: logging must never crash or slow the app it serves.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;

    /// <summary>Full path of the current session log, or null until initialized.</summary>
    public static string? FilePath => _path;

    /// <summary>
    /// (Re)opens the log, discarding any previous content. Called once at startup.
    /// </summary>
    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
                _path = null;

                var dir = directory ?? DefaultDirectory();
                Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "quotalens.log");
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream)
                {
                    AutoFlush = true,
                };
                _path = path;
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception error) =>
        Write("ERROR", message + Environment.NewLine + error);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                _writer?.WriteLine(line);
            }
            catch
            {
                // Best-effort only.
            }
        }
    }

    internal static string DefaultDirectory()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        return System.IO.Path.Combine(localAppData, "QuotaLens", "logs");
    }
}
