using System.Text;

namespace AppLockerOverlay;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "windlock.log");

    public static void Log(string message, string? debugOnlyPath = null)
    {
#if DEBUG
        if (!string.IsNullOrEmpty(debugOnlyPath))
        {
            message = $"{message} Config: {debugOnlyPath}";
        }
#endif
        WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }

    public static void LogException(string context, Exception ex)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}");
        builder.AppendLine(ex.ToString());
        WriteRaw(builder.ToString());
    }

    private static void WriteLine(string line)
    {
        WriteRaw(line + Environment.NewLine);
    }

    private static void WriteRaw(string text)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath, text);
            }
        }
        catch
        {
            // Never crash the app because logging failed.
        }
    }
}
