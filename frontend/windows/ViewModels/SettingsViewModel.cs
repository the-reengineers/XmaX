using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.ViewModels;

/// <summary>
/// ViewModel for the Settings page. Manages app configuration: language, theme,
/// persist, auto-start, widget layout, and system defaults.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly PipeClient _pipe;
    private readonly WidgetService _widgetService;

    // Config state
    private AppConfig _config = new();

    public SettingsViewModel(PipeClient pipe, WidgetService widgetService)
    {
        _pipe = pipe;
        _widgetService = widgetService;

        // Load config on creation
        _ = LoadConfigAsync();
    }

    // ===== Observable config properties =====

    /// <summary>Current app config.</summary>
    public AppConfig Config
    {
        get => _config;
        private set
        {
            if (ReferenceEquals(_config, value)) return;
            _config = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Config)));
        }
    }

    /// <summary>Language: "auto", "en", or "zh".</summary>
    public string Language
    {
        get => _config.Language;
        set
        {
            if (_config.Language == value) return;
            _config.Language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            _ = SaveConfigFieldAsync("language", value);
        }
    }

    /// <summary>Theme: "system", "light", or "dark".</summary>
    public string Theme
    {
        get => _config.Theme;
        set
        {
            if (_config.Theme == value) return;
            _config.Theme = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Theme)));
            _ = SaveConfigFieldAsync("theme", value);
        }
    }

    /// <summary>Persist toggle: apply user settings on startup.</summary>
    public bool Persist
    {
        get => _config.Persist;
        set
        {
            if (_config.Persist == value) return;
            _config.Persist = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Persist)));
            _ = SaveConfigFieldAsync("persist", value);
        }
    }

    /// <summary>Session persist toggle: apply settings for this session only (not saved to disk).</summary>
    public bool SessionPersist
    {
        get => _config.SessionPersist;
        set
        {
            if (_config.SessionPersist == value) return;
            _config.SessionPersist = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionPersist)));
            _ = SaveConfigFieldAsync("session_persist", value);
        }
    }

    /// <summary>Auto-start toggle: launch at user logon.</summary>
    public bool AutoStart
    {
        get => _config.AutoStart;
        set
        {
            if (_config.AutoStart == value) return;
            _config.AutoStart = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoStart)));
            _ = SaveConfigFieldAsync("auto_start", value);
        }
    }

    /// <summary>Column count for home page grid (3–5).</summary>
    public int Columns
    {
        get => _widgetService.Columns;
        set
        {
            if (_widgetService.Columns == value) return;
            _widgetService.Columns = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Columns)));
            _ = _widgetService.SaveLayoutAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ===== Config persistence =====

    /// <summary>Load config from backend.</summary>
    public async Task LoadConfigAsync()
    {
        try
        {
            var data = await _pipe.SendCommandAsync("get_config").ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<AppConfig>(
                data.ToJsonString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (config != null)
            {
                Config = config;

                // Load widget layout into WidgetService
                _widgetService.LoadLayout(config.HomeLayout);
            }
        }
        catch
        {
            // Failed to load config — use defaults
        }
    }

    /// <summary>Save a single config field to backend.</summary>
    private async Task SaveConfigFieldAsync(string fieldName, object value)
    {
        try
        {
            var payload = new JsonObject();

            // Convert value to appropriate JSON type
            if (value is string s)
                payload[fieldName] = s;
            else if (value is bool b)
                payload[fieldName] = b;
            else if (value is int i)
                payload[fieldName] = i;
            else
                payload[fieldName] = JsonSerializer.SerializeToNode(value);

            await _pipe.SendCommandAsync("set_config", payload).ConfigureAwait(false);
        }
        catch
        {
            // Save failed — config is out of sync, but UI is updated
        }
    }

    // ===== Widget layout management =====

    /// <summary>Toggle visibility of a widget by ID.</summary>
    public void ToggleWidgetVisibility(string widgetId)
    {
        _widgetService.ToggleVisible(widgetId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"WidgetVisibility[{widgetId}]"));
        _ = _widgetService.SaveLayoutAsync();
    }

    /// <summary>Move a widget up in the display order.</summary>
    public void MoveWidgetUp(string widgetId)
    {
        _widgetService.MoveUp(widgetId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetOrder)));
        _ = _widgetService.SaveLayoutAsync();
    }

    /// <summary>Move a widget down in the display order.</summary>
    public void MoveWidgetDown(string widgetId)
    {
        _widgetService.MoveDown(widgetId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetOrder)));
        _ = _widgetService.SaveLayoutAsync();
    }

    /// <summary>Get all registered widget IDs in current order.</summary>
    public IReadOnlyList<string> WidgetOrder => _widgetService.WidgetOrder;

    /// <summary>Check if a widget is visible.</summary>
    public bool IsWidgetVisible(string widgetId) => _widgetService.IsVisible(widgetId);

    // ===== System defaults =====

    /// <summary>
    /// Revert to system defaults. Sends a special command to backend to reset all settings.
    /// Note: Backend must implement this command. For now, this is a placeholder.
    /// </summary>
    public async Task RevertToDefaultsAsync()
    {
        // TODO: Backend needs to implement a "reset_to_defaults" command
        // For now, we'll just reload the config (which will use backend defaults if config is deleted)
        await LoadConfigAsync().ConfigureAwait(false);
    }
}
