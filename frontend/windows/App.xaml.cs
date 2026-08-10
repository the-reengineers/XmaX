using System.Text.Json;
using Microsoft.UI.Xaml;
using XmaX.Models;
using XmaX.Services;

namespace XmaX;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xmax", "frontend_crash.log");

        // Crash logging — overwrite on each startup, append within session
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, $"[{DateTime.Now}] Session started\n");
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] Unhandled: {e.ExceptionObject}\n");
            }
            catch { }
        };

        Microsoft.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.Now}] XAML Unhandled: {e.Exception}\n");
            }
            catch { }
        };

        this.InitializeComponent();

        // Initialize localization before any UI is loaded
        InitializeLanguage();
    }

    /// <summary>
    /// Load language preference from config and set Loc language.
    /// </summary>
    private static void InitializeLanguage()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "xmax",
                "config.json");

            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config?.Language != null)
                {
                    Loc.SetLanguage(config.Language);
                    return;
                }
            }
        }
        catch
        {
            // Failed to load config — fall through to auto
        }

        // Default: auto-detect system language
        Loc.SetLanguage("auto");
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Initialize services
        Pipe = new PipeClient();
        MetricsService = new MetricsService(Pipe);
        ProfileService = new ProfileService(Pipe);
        AutoTuneService = new AutoTuneService(Pipe);
        WidgetService = new WidgetService(Pipe);

        // Connect to backend
        Pipe.Connect();

        m_window = new MainWindow();
        m_window.Activate();
    }

    private Window? m_window;

    // ===== Service accessors =====

    /// <summary>Backend IPC pipe client.</summary>
    public static PipeClient Pipe { get; private set; } = null!;

    /// <summary>Metrics monitoring service.</summary>
    public static MetricsService MetricsService { get; private set; } = null!;

    /// <summary>Profile and fan curve management service.</summary>
    public static ProfileService ProfileService { get; private set; } = null!;

    /// <summary>Adaptive controller service.</summary>
    public static AutoTuneService AutoTuneService { get; private set; } = null!;

    /// <summary>Widget layout management service.</summary>
    public static WidgetService WidgetService { get; private set; } = null!;
}
