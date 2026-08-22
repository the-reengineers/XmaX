using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using XmaX.Models;
using XmaX.Services;
using XmaX.ViewModels;

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
        // Parse --debug flag from command line
        bool debugEnabled = Environment.GetCommandLineArgs().Contains("--debug");

        // Initialize logger (attaches to parent console if --debug)
        Logger.Init(debugEnabled);

        if (debugEnabled) Logger.Info("Frontend starting with debug logging enabled");

        var crashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xmax", "frontend_crash.log");

        // Crash logging — overwrite on each startup, append within session
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!);
            File.WriteAllText(crashLogPath, $"[{DateTime.Now}] Session started\n");
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(crashLogPath, $"[{DateTime.Now}] Unhandled: {e.ExceptionObject}\n");
            }
            catch { }
            Logger.Error($"Unhandled exception: {e.ExceptionObject}");
        };

        Microsoft.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(crashLogPath, $"[{DateTime.Now}] XAML Unhandled: {e.Exception}\n");
            }
            catch { }
            Logger.Error($"XAML Unhandled exception: {e.Exception}");
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
        WidgetService = new WidgetService(Pipe);
        UmaService = new UmaService(Pipe);

        // Connect to backend
        Pipe.Connect();

        m_window = new MainWindow();
        m_window.Activate();
    }

    private static MainWindow? m_window;
    private static HomeEditorWindow? m_editorWindow;

    /// <summary>Main application window.</summary>
    public static MainWindow? MainWindow => m_window;

    /// <summary>Home editor window (shown in edit mode).</summary>
    public static HomeEditorWindow? EditorWindow => m_editorWindow;

    /// <summary>Create and show the home editor window.</summary>
    public static void ShowEditorWindow()
    {
        if (m_editorWindow == null)
        {
            m_editorWindow = new HomeEditorWindow();
        }
        m_editorWindow.ShowWindow();
    }

    /// <summary>Hide the home editor window.</summary>
    public static void HideEditorWindow()
    {
        m_editorWindow?.HideWindow();
    }

    // ===== Service accessors =====

    /// <summary>Backend IPC pipe client.</summary>
    public static PipeClient Pipe { get; private set; } = null!;

    /// <summary>Metrics monitoring service.</summary>
    public static MetricsService MetricsService { get; private set; } = null!;

    /// <summary>Profile and fan curve management service.</summary>
    public static ProfileService ProfileService { get; private set; } = null!;

    /// <summary>Widget layout management service.</summary>
    public static WidgetService WidgetService { get; private set; } = null!;

    /// <summary>UMA (Variable Graphics Memory) management service.</summary>
    public static UmaService UmaService { get; private set; } = null!;

    // ===== Shared ViewModel accessors =====

    private static ProfilesViewModel? _profilesViewModel;

    /// <summary>Get or create the shared ProfilesViewModel (used by Profiles and PowerStates sub-pages).</summary>
    public static ProfilesViewModel GetProfilesViewModel()
    {
        _profilesViewModel ??= new ProfilesViewModel(ProfileService);
        return _profilesViewModel;
    }

    /// <summary>Navigate the main frame to a page type with an optional transition.</summary>
    public static void NavigateTo(Type pageType, NavigationTransitionInfo? transitionInfo = null)
    {
        m_window?.NavigateToPage(pageType, transitionInfo);
    }

    /// <summary>Navigate the main frame back to the previous page.</summary>
    public static void NavigateBack()
    {
        m_window?.GoBack();
    }
}
