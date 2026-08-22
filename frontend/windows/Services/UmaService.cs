using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using XmaX.Models;

namespace XmaX.Services;

/// <summary>
/// Service for managing UMA (Variable Graphics Memory) presets.
/// Wraps pipe commands for reading and setting UMA options.
/// </summary>
public sealed class UmaService : INotifyPropertyChanged
{
    private readonly PipeClient _pipe;
    private bool _supported;
    private List<UmaOption> _availableOptions = new();
    private UmaOption? _currentOption;

    public UmaService(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.Connected += OnConnected;
    }

    /// <summary>Whether Variable Graphics Memory is supported on this system.</summary>
    public bool Supported
    {
        get => _supported;
        private set
        {
            if (_supported == value) return;
            _supported = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Supported)));
        }
    }

    /// <summary>List of available UMA presets.</summary>
    public List<UmaOption> AvailableOptions
    {
        get => _availableOptions;
        private set
        {
            _availableOptions = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableOptions)));
        }
    }

    /// <summary>Currently selected UMA option.</summary>
    public UmaOption? CurrentOption
    {
        get => _currentOption;
        private set
        {
            if (_currentOption?.Name == value?.Name) return;
            _currentOption = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentOption)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fetch UMA options from the backend.
    /// Call on connect to populate the available options.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            Logger.Debug("[UMA] RefreshAsync: sending get_uma_options command");
            // SendCommandAsync returns the inner "data" object (HandleResponse strips "ok"/"id")
            // On error it throws InvalidOperationException (caught below)
            var data = await _pipe.SendCommandAsync("get_uma_options").ConfigureAwait(false);
            Logger.Debug($"[UMA] RefreshAsync: received data, keys: {string.Join(",", data.Select(k => k.Key))}");

            var supported = data["supported"]?.GetValue<bool>() ?? false;
            Logger.Debug($"[UMA] RefreshAsync: supported={supported}");
            Supported = supported;

            if (!Supported)
            {
                AvailableOptions = new();
                CurrentOption = null;
                return;
            }

            // Parse available options
            var options = new List<UmaOption>();
            var optionsArray = data["available_options"]?.AsArray();
            Logger.Debug($"[UMA] RefreshAsync: available_options array {(optionsArray != null ? $"has {optionsArray.Count} items" : "is null")}");
            if (optionsArray != null)
            {
                foreach (var optNode in optionsArray)
                {
                    if (optNode == null) continue;
                    var opt = new UmaOption
                    {
                        Id = optNode["id"]?.GetValue<string>() ?? string.Empty,
                        Name = optNode["name"]?.GetValue<string>() ?? string.Empty,
                        Mode = optNode["mode"]?.GetValue<string>() ?? "auto",
                        MemoryCarvedGb = optNode["memory_carved_gb"]?.GetValue<double>() ?? 0,
                        MemoryRemainingGb = optNode["memory_remaining_gb"]?.GetValue<double>() ?? 0
                    };
                    options.Add(opt);
                    Logger.Debug($"[UMA] RefreshAsync: parsed option id='{opt.Id}' name='{opt.Name}' mode={opt.Mode} carved={opt.MemoryCarvedGb}");
                }
            }
            AvailableOptions = options;
            Logger.Debug($"[UMA] RefreshAsync: set AvailableOptions to {options.Count} items");

            // Parse current option
            var currentNode = data["current_option"];
            if (currentNode != null)
            {
                CurrentOption = new UmaOption
                {
                    Id = currentNode["id"]?.GetValue<string>() ?? string.Empty,
                    Name = currentNode["name"]?.GetValue<string>() ?? string.Empty,
                    Mode = currentNode["mode"]?.GetValue<string>() ?? "auto",
                    MemoryCarvedGb = currentNode["memory_carved_gb"]?.GetValue<double>() ?? 0,
                    MemoryRemainingGb = currentNode["memory_remaining_gb"]?.GetValue<double>() ?? 0
                };
                Logger.Debug($"[UMA] RefreshAsync: current option id='{CurrentOption.Id}' name='{CurrentOption.Name}'");

                // Mark the current option in the list (match by unique Id)
                foreach (var opt in AvailableOptions)
                {
                    opt.IsSelected = opt.Id == CurrentOption.Id;
                }
            }
            else
            {
                Logger.Debug("[UMA] RefreshAsync: no current_option in response");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[UMA] RefreshAsync: exception: {ex.Message}");
            Supported = false;
            AvailableOptions = new();
            CurrentOption = null;
        }
    }

    /// <summary>
    /// Set the UMA option by unique id. Triggers a system reboot.
    /// </summary>
    /// <param name="optionId">Unique id of the option to select (e.g., "auto:0.0", "custom:2.0").</param>
    /// <returns>True if the option was set successfully (reboot required).</returns>
    public async Task<bool> SetOptionAsync(string optionId)
    {
        try
        {
            var payload = new JsonObject
            {
                ["option_id"] = optionId
            };

            // SendCommandAsync returns the inner "data" object on success,
            // or throws InvalidOperationException on error (caught below)
            await _pipe.SendCommandAsync("set_uma_option", payload).ConfigureAwait(false);

            // Update current option locally
            var newCurrent = AvailableOptions.FirstOrDefault(o => o.Id == optionId);
            if (newCurrent != null)
            {
                foreach (var opt in AvailableOptions)
                {
                    opt.IsSelected = opt.Id == optionId;
                }
                CurrentOption = new UmaOption
                {
                    Id = newCurrent.Id,
                    Name = newCurrent.Name,
                    Mode = newCurrent.Mode,
                    MemoryCarvedGb = newCurrent.MemoryCarvedGb,
                    MemoryRemainingGb = newCurrent.MemoryRemainingGb
                };
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void OnConnected()
    {
        Logger.Debug("[UMA] OnConnected: pipe connected, refreshing UMA options");
        await RefreshAsync().ConfigureAwait(false);
    }
}
