using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using XmaX.Models;
using XmaX.ViewModels;

namespace XmaX.Pages;

/// <summary>
/// Page for creating or editing a profile (fixed or adaptive).
/// Navigated from ProfilesSubPage via SettingsPage breadcrumb navigation.
/// Saves directly to ProfileService — caller refreshes on return.
/// </summary>
public sealed partial class ProfileEditorPage : Page
{
    private Profile? _existingProfile;
    private bool _isNew;
    private string? _editingProfileId;
    private readonly List<FanCurve> _fanCurves;
    private readonly List<Profile> _existingProfiles;
    private bool _isDirty;
    private bool _isLoading;
    private bool _adaptivePrefillApplied;

    /// <summary>
    /// Tuning presets — value bundles matching the handheld ecosystem convention
    /// (OneXConsole, ASUS, Legion Go, power-profiles-daemon). Selecting a preset
    /// pre-fills the adaptive sliders; tweaking them afterwards makes the profile
    /// custom.  Mirrors the GNOME prefs editor and backend preset_defaults().
    /// Order: quiet, balanced, performance — indexes must match TuningCombo items.
    /// </summary>
    private static readonly (int TargetTemp, int TdpMax, int FanMax, int FanMin)[] PresetBundles =
    {
        (80, 25, 45, 20),    // quiet
        (85, 45, 80, 25),    // balanced
        (90, 100, 100, 30),  // performance
    };

    /// <summary>
    /// Sentinel combo entry meaning "no fan curve" — the backend leaves the
    /// fan in Auto mode, i.e. the EC firmware's own hardware curve. Its Id is
    /// the empty string, which both save paths treat as "no curve".
    /// </summary>
    private static readonly FanCurve AutoFanCurve = new() { Id = "", Name = "Auto (hardware)" };

    public bool IsDirty => _isDirty;

    public void MarkAsClean() => _isDirty = false;

    public ProfileEditorPage()
    {
        this.InitializeComponent();
        _fanCurves = App.ProfileService.FanCurves.ToList();
        _existingProfiles = App.ProfileService.Profiles.ToList();

        // Localize UI
        NameBox.Header = Loc.Form_Name;
        NameBox.PlaceholderText = Loc.Form_ProfileName;

        TypeCombo.Header = Loc.Form_ProfileType;
        TypeCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_TypeFixed, Tag = "fixed" });
        TypeCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_TypeAdaptive, Tag = "adaptive" });
        TypeCombo.SelectedIndex = 0;
        TypeCombo.SelectionChanged += OnTypeChanged;

        PowerStateCombo.Header = Loc.Form_PowerState;
        PowerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_None, Tag = "" });
        PowerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_Battery, Tag = "battery" });
        PowerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_UsbCSlow, Tag = "usb_c_slow" });
        PowerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_UsbCFast, Tag = "usb_c_fast" });
        PowerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_DcIn, Tag = "dc_in" });
        PowerStateCombo.SelectedIndex = 0;
        PowerStateCombo.SelectionChanged += OnPowerStateChanged;

        DefaultCheckBox.Content = "Default for this power state";

        StapmSlider.Header = Loc.Form_Stapm;
        FastSlider.Header = Loc.Form_Fast;
        SlowSlider.Header = Loc.Form_Slow;

        FanCurveCombo.Header = Loc.Form_FanCurve;
        FanCurveCombo.DisplayMemberPath = nameof(FanCurve.Name);
        // Auto sentinel first, then the daemon's user curves (there is no
        // builtin curve anymore — the hardware curve IS the default).
        FanCurveCombo.Items.Add(AutoFanCurve);
        foreach (var fc in _fanCurves)
        {
            FanCurveCombo.Items.Add(fc);
        }

        TuningCombo.Header = Loc.Form_Tuning;
        TuningCombo.Items.Add(new ComboBoxItem { Content = Loc.Button_Quiet, Tag = "quiet" });
        TuningCombo.Items.Add(new ComboBoxItem { Content = Loc.Button_Balanced, Tag = "balanced" });
        TuningCombo.Items.Add(new ComboBoxItem { Content = Loc.Button_Performance, Tag = "performance" });
        TuningCombo.SelectedIndex = 1; // Balanced
        TuningCombo.SelectionChanged += OnTuningChanged;

        TargetTempSlider.Header = Loc.Form_TargetTemp;
        TdpMaxSlider.Header = Loc.Form_TdpMax;
        FanMaxSlider.Header = Loc.Form_FanMax;
        FanMinSlider.Header = Loc.Form_FanMin;

        CancelButton.Content = Loc.Button_Cancel;
        SaveButton.Content = Loc.Button_Save;

        // Dirty tracking
        NameBox.TextChanged += MarkDirty;
        DefaultCheckBox.Checked += MarkDirty;
        DefaultCheckBox.Unchecked += MarkDirty;
        StapmSlider.ValueChanged += MarkDirty;
        FastSlider.ValueChanged += MarkDirty;
        SlowSlider.ValueChanged += MarkDirty;
        FanCurveCombo.SelectionChanged += MarkDirty;
        TargetTempSlider.ValueChanged += MarkDirty;
        TdpMaxSlider.ValueChanged += MarkDirty;
        FanMaxSlider.ValueChanged += MarkDirty;
        FanMinSlider.ValueChanged += MarkDirty;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _existingProfile = e.Parameter as Profile;
        _isNew = _existingProfile == null;
        _editingProfileId = _existingProfile?.Id;

        _isLoading = true;
        PopulateFields(_existingProfile);
        _isLoading = false;
        _isDirty = false;
    }

    private void PopulateFields(Profile? existingProfile)
    {
        if (existingProfile == null)
        {
            // New profile defaults
            StapmSlider.Value = 45;
            FastSlider.Value = 50;
            SlowSlider.Value = 45;
            FanCurveCombo.SelectedItem = AutoFanCurve;
            return;
        }

        NameBox.Text = existingProfile.Name;

        // Set type
        TypeCombo.SelectedIndex = existingProfile.IsAdaptive ? 1 : 0;

        // Set power state
        if (!string.IsNullOrEmpty(existingProfile.PowerState))
        {
            for (int i = 0; i < PowerStateCombo.Items.Count; i++)
            {
                if (PowerStateCombo.Items[i] is ComboBoxItem item && (string?)item.Tag == existingProfile.PowerState)
                {
                    PowerStateCombo.SelectedIndex = i;
                    break;
                }
            }
            // Show the default checkbox and set its state
            DefaultCheckBox.Visibility = Visibility.Visible;
            DefaultCheckBox.IsChecked = existingProfile.IsDefault;
        }

        if (existingProfile.IsAdaptive)
        {
            // Set adaptive fields. The backend normalizes legacy tuning names
            // on load; map any leftovers here so the combo still resolves.
            var tuning = existingProfile.Tuning switch
            {
                "silent" => "quiet",
                "default" => "balanced",
                _ => existingProfile.Tuning,
            };
            for (int i = 0; i < TuningCombo.Items.Count; i++)
            {
                if (TuningCombo.Items[i] is ComboBoxItem item && (string?)item.Tag == tuning)
                {
                    TuningCombo.SelectedIndex = i;
                    break;
                }
            }
            TargetTempSlider.Value = existingProfile.TargetTempC;
            TdpMaxSlider.Value = existingProfile.TdpMaxW;
            FanMaxSlider.Value = existingProfile.FanMaxPercent;
            FanMinSlider.Value = existingProfile.FanMinPercent;
            // Values came from the profile — don't clobber them with preset
            // defaults when the type combo fires change events.
            _adaptivePrefillApplied = true;
        }
        else
        {
            // Set fixed fields
            StapmSlider.Value = existingProfile.Tdp.Stapm;
            FastSlider.Value = existingProfile.Tdp.Fast;
            SlowSlider.Value = existingProfile.Tdp.Slow;
            // Empty or dangling curve reference (e.g. the deprecated builtin
            // "default", which the backend now migrates away) -> Auto.
            FanCurveCombo.SelectedItem =
                string.IsNullOrEmpty(existingProfile.FanCurve)
                    ? AutoFanCurve
                    : _fanCurves.FirstOrDefault(f => f.Id == existingProfile.FanCurve) ?? AutoFanCurve;
        }
    }

    /// <summary>
    /// Get the page title for breadcrumb navigation.
    /// </summary>
    public string GetPageTitle()
    {
        if (_isNew)
        {
            return Loc.Dialog_CreateProfile;
        }
        return Loc.Dialog_EditProfile;
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        var isAdaptive = TypeCombo.SelectedIndex == 1;
        FixedPanel.Visibility = isAdaptive ? Visibility.Collapsed : Visibility.Visible;
        AdaptivePanel.Visibility = isAdaptive ? Visibility.Visible : Visibility.Collapsed;
        // A brand-new profile switching to Adaptive starts from the current
        // preset's value bundle (existing profiles skip this — their values
        // were populated from the profile itself).
        if (isAdaptive && !_isLoading && !_adaptivePrefillApplied)
        {
            ApplyPresetBundles();
            _adaptivePrefillApplied = true;
        }
        if (!_isLoading) _isDirty = true;
    }

    private void OnTuningChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selecting a preset loads its value bundle into the sliders.  The
        // preset is a starting point, not a hidden cap: every value stays
        // editable afterwards ("custom").
        if (!_isLoading)
        {
            ApplyPresetBundles();
            MarkDirty(sender, e);
        }
    }

    private void ApplyPresetBundles()
    {
        var idx = TuningCombo.SelectedIndex;
        if (idx < 0 || idx >= PresetBundles.Length) return;
        var bundle = PresetBundles[idx];
        TargetTempSlider.Value = bundle.TargetTemp;
        TdpMaxSlider.Value = bundle.TdpMax;
        FanMaxSlider.Value = bundle.FanMax;
        FanMinSlider.Value = bundle.FanMin;
    }

    private void OnPowerStateChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show the "Default" checkbox only when a power state is selected
        var hasPowerState = PowerStateCombo.SelectedItem is ComboBoxItem psItem
            && psItem.Tag is string psTag
            && !string.IsNullOrEmpty(psTag);
        DefaultCheckBox.Visibility = hasPowerState ? Visibility.Visible : Visibility.Collapsed;
        if (!_isLoading) _isDirty = true;
    }

    private void MarkDirty(object sender, object e)
    {
        if (!_isLoading) _isDirty = true;
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            if (!await ShowUnsavedChangesDialogAsync())
                return;
        }
        if (Frame.CanGoBack)
        {
            Frame.GoBack(new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowNameError();
            return;
        }

        var isAdaptive = TypeCombo.SelectedIndex == 1;

        // Validate fan curve for fixed profiles
        string? fanCurveId = null;
        if (!isAdaptive)
        {
            if (FanCurveCombo.SelectedItem is not FanCurve selectedCurve)
            {
                ShowFanCurveError();
                return;
            }
            // The Auto sentinel (empty Id) means "no curve" -> fan Auto mode.
            fanCurveId = string.IsNullOrEmpty(selectedCurve.Id) ? null : selectedCurve.Id;
        }

        // Get power state
        string? powerState = null;
        if (PowerStateCombo.SelectedItem is ComboBoxItem psItem && psItem.Tag is string psTag && !string.IsNullOrEmpty(psTag))
        {
            powerState = psTag;
        }

        // Get is_default (only meaningful when a power state is assigned)
        bool isDefault = powerState != null && DefaultCheckBox.IsChecked == true;

        try
        {
            var viewModel = App.GetProfilesViewModel();

            if (_isNew)
            {
                // Create new profile
                if (isAdaptive)
                {
                    var tuning = (TuningCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "balanced";
                    // Floor can never exceed the ceiling (backend validates too).
                    var fanMin = Math.Min((int)FanMinSlider.Value, (int)FanMaxSlider.Value);
                    await viewModel.CreateAdaptiveProfileAsync(
                        name,
                        tuning,
                        (int)TargetTempSlider.Value,
                        (int)TdpMaxSlider.Value,
                        (int)FanMaxSlider.Value,
                        fanMin,
                        powerState);
                }
                else
                {
                    await viewModel.CreateFixedProfileAsync(
                        name,
                        (int)StapmSlider.Value,
                        (int)FastSlider.Value,
                        (int)SlowSlider.Value,
                        fanCurveId,
                        powerState);
                }
            }
            else
            {
                // Update existing profile
                Profile profile;
                if (isAdaptive)
                {
                    var tuning = (TuningCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "balanced";
                    profile = new Profile
                    {
                        Id = _editingProfileId ?? "",
                        Name = name,
                        Type = "adaptive",
                        PowerState = powerState,
                        IsDefault = isDefault,
                        Tuning = tuning,
                        TargetTempC = (int)TargetTempSlider.Value,
                        TdpMaxW = (int)TdpMaxSlider.Value,
                        FanMaxPercent = (int)FanMaxSlider.Value,
                        FanMinPercent = Math.Min((int)FanMinSlider.Value, (int)FanMaxSlider.Value),
                    };
                }
                else
                {
                    profile = new Profile
                    {
                        Id = _editingProfileId ?? "",
                        Name = name,
                        Type = "fixed",
                        PowerState = powerState,
                        IsDefault = isDefault,
                        Tdp = new TdpLimits
                        {
                            Stapm = (int)StapmSlider.Value,
                            Fast = (int)FastSlider.Value,
                            Slow = (int)SlowSlider.Value,
                        },
                        FanCurve = fanCurveId ?? "",
                    };
                }
                await viewModel.UpdateProfileAsync(profile);
            }

            // Mark as clean before navigating back to avoid unsaved changes dialog
            _isDirty = false;

            // Navigate back — ProfilesSubPage will refresh via ViewModel.PropertyChanged
            if (Frame.CanGoBack)
            {
                Frame.GoBack(new SlideNavigationTransitionInfo
                {
                    Effect = SlideNavigationTransitionEffect.FromRight
                });
            }
        }
        catch (Exception ex)
        {
            ShowError(_isNew ? Loc.Dialog_CreateFailed : Loc.Dialog_UpdateFailed, ex.Message);
        }
    }

    private void ShowNameError()
    {
        ShowError(Loc.Dialog_Error, Loc.Form_ProfileName);
    }

    private void ShowFanCurveError()
    {
        ShowError(Loc.Dialog_Error, Loc.Form_FanCurve);
    }

    private void ShowError(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Loc.Button_Ok,
            XamlRoot = this.XamlRoot,
        };
        _ = dialog.ShowAsync();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        if (_isDirty)
        {
            e.Cancel = true;
            _ = ShowUnsavedChangesDialogAsync().ContinueWith(task =>
            {
                if (task.Result)
                {
                    _isDirty = false;
                    Frame.GoBack();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            base.OnNavigatingFrom(e);
        }
    }

    private async Task<bool> ShowUnsavedChangesDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Dialog_UnsavedChanges,
            Content = Loc.Dialog_UnsavedChangesMessage,
            PrimaryButtonText = Loc.Button_Discard,
            CloseButtonText = Loc.Button_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        App.MainWindow?.SetModalDialogOpen(true);
        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        finally
        {
            App.MainWindow?.SetModalDialogOpen(false);
        }
    }
}
