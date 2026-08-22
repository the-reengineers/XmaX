using System.Runtime.InteropServices;

namespace XmaX.Services;

/// <summary>
/// Simple logger for the frontend. Writes to a log file always,
/// and optionally to the parent console when --debug is enabled.
/// </summary>
public static class Logger
{
    private static bool _debugEnabled;
    private static string _logPath = "";
    private static readonly object _lock = new();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    /// Initialize the logger. Call once at startup.
    /// </summary>
    /// <param name="debugEnabled">If true, attach to parent console and output debug logs.</param>
    public static void Init(bool debugEnabled)
    {
        _debugEnabled = debugEnabled;

        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xmax", "frontend.log");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            // Overwrite on each startup
            File.WriteAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Session started\n");
        }
        catch { }

        if (_debugEnabled)
        {
            // Attach to the parent process's console so our output appears in the terminal
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
        }
    }

    /// <summary>Release the console attachment on shutdown.</summary>
    public static void Shutdown()
    {
        if (_debugEnabled)
        {
            try { FreeConsole(); } catch { }
        }
    }

    public static void Debug(string message)
    {
        if (!_debugEnabled) return;
        var formatted = Format("DEBUG", message);
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }

    public static void Info(string message)
    {
        var formatted = Format("INFO", message);
        WriteToFile(formatted);
        if (_debugEnabled) WriteToConsole(formatted);
    }

    public static void Warn(string message)
    {
        var formatted = Format("WARN", message);
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }

    public static void Error(string message)
    {
        var formatted = Format("ERROR", message);
        WriteToFile(formatted);
        WriteToConsole(formatted);
    }

    private static string Format(string level, string message)
    {
        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
    }

    private static void WriteToFile(string formatted)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, formatted + "\n");
            }
            catch { }
        }
    }

    private static void WriteToConsole(string formatted)
    {
        try
        {
            Console.Error.WriteLine(formatted);
        }
        catch { }
    }
}
