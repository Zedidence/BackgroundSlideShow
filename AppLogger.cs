using System.IO;

namespace BackgroundSlideShow;

/// <summary>
/// Minimal file logger. Writes to %LOCALAPPDATA%\BackgroundSlideShow\logs\YYYY-MM-DD.log
/// </summary>
internal static class AppLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BackgroundSlideShow", "logs");

    private static string LogFile =>
        Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log");

    static AppLogger()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}  {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        try
        {
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Can't log the logger failing — write to debug output as a last resort
            System.Diagnostics.Debug.WriteLine($"AppLogger failed: {ex.Message}");
        }
    }

    /// <summary>Path to today's log file, for display purposes.</summary>
    public static string CurrentLogPath => LogFile;
}
