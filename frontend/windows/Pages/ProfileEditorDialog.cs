using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;

namespace XmaX.Pages;

/// <summary>
/// Dialog for creating or editing a profile (fixed or adaptive).
/// </summary>
public sealed class ProfileEditorDialog : ContentDialog
{
    private readonly bool _isEdit;
    private readonly List<FanCurve> _fanCurves;
    private readonly List<Profile> _existingProfiles;
    private readonly string? _editingProfileId;

    private TextBox _nameBox = null!;
    private ComboBox _typeCombo = null!;
    private ComboBox _powerStateCombo = null!;
    private CheckBox _defaultCheckBox = null!;

    // Fixed profile controls
    private StackPanel _fixedPanel = null!;
    private Slider _stapmSlider = null!;
    private Slider _fastSlider = null!;
    private Slider _slowSlider = null!;
    private ComboBox _fanCurveCombo = null!;

    // Adaptive profile controls
    private StackPanel _adaptivePanel = null!;
    private ComboBox _tuningCombo = null!;
    private Slider _targetTempSlider = null!;
    private Slider _tdpMaxSlider = null!;
    private Slider _fanMaxSlider = null!;

    /// <summary>The resulting profile after OK, or null if cancelled.</summary>
    public Profile? ResultProfile { get; private set; }

    public ProfileEditorDialog(Profile? existingProfile, List<FanCurve> fanCurves, List<Profile>? existingProfiles = null)
    {
        _isEdit = existingProfile != null;
        _fanCurves = fanCurves;
        _existingProfiles = existingProfiles ?? new List<Profile>();
        _editingProfileId = existingProfile?.Id;

        Title = _isEdit ? Loc.Dialog_EditProfile : Loc.Dialog_CreateProfile;
        PrimaryButtonText = _isEdit ? Loc.Button_Save : Loc.Button_Create;
        CloseButtonText = Loc.Button_Cancel;
        DefaultButton = ContentDialogButton.Primary;

        InitializeContent();
        PopulateFields(existingProfile);

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void InitializeContent()
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };

        // Name
        _nameBox = new TextBox { Header = Loc.Form_Name, PlaceholderText = Loc.Form_ProfileName };
        panel.Children.Add(_nameBox);

        // Type selector
        _typeCombo = new ComboBox
        {
            Header = Loc.Form_ProfileType,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _typeCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_TypeFixed, Tag = "fixed" });
        _typeCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_TypeAdaptive, Tag = "adaptive" });
        _typeCombo.SelectedIndex = 0;
        _typeCombo.SelectionChanged += OnTypeChanged;
        panel.Children.Add(_typeCombo);

        // Power state assignment
        _powerStateCombo = new ComboBox
        {
            Header = Loc.Form_PowerState,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _powerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_None, Tag = "" });
        _powerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_Battery, Tag = "battery" });
        _powerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_UsbCSlow, Tag = "usb_c_slow" });
        _powerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_UsbCFast, Tag = "usb_c_fast" });
        _powerStateCombo.Items.Add(new ComboBoxItem { Content = Loc.Power_DcIn, Tag = "dc_in" });
        _powerStateCombo.SelectedIndex = 0;
        _powerStateCombo.SelectionChanged += OnPowerStateChanged;
        panel.Children.Add(_powerStateCombo);

        // Default profile checkbox (only visible when a power state is selected)
        _defaultCheckBox = new CheckBox
        {
            Content = "Default for this power state",
            IsChecked = false,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(_defaultCheckBox);

        // Fixed profile panel
        _fixedPanel = new StackPanel { Spacing = 12 };

        _stapmSlider = new Slider { Header = Loc.Form_Stapm, Minimum = 6, Maximum = 120, StepFrequency = 1 };
        _fixedPanel.Children.Add(_stapmSlider);

        _fastSlider = new Slider { Header = Loc.Form_Fast, Minimum = 6, Maximum = 120, StepFrequency = 1 };
        _fixedPanel.Children.Add(_fastSlider);

        _slowSlider = new Slider { Header = Loc.Form_Slow, Minimum = 6, Maximum = 120, StepFrequency = 1 };
        _fixedPanel.Children.Add(_slowSlider);

        _fanCurveCombo = new ComboBox
        {
            Header = Loc.Form_FanCurve,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(FanCurve.Name),
        };
        foreach (var fc in _fanCurves)
        {
            _fanCurveCombo.Items.Add(fc);
        }
        _fixedPanel.Children.Add(_fanCurveCombo);

        panel.Children.Add(_fixedPanel);

        // Adaptive profile panel
        _adaptivePanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };

        _tuningCombo = new ComboBox
        {
            Header = Loc.Form_Tuning,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _tuningCombo.Items.Add(new ComboBoxItem { Content = Loc.Button_Silent, Tag = "silent" });
        _tuningCombo.Items.Add(new ComboBoxItem { Content = Loc.Button_Default, Tag = "default" });
        _tuningCombo.Items.Add(new ComboBoxItem { Content = Loc.Button_Performance, Tag = "performance" });
        _tuningCombo.SelectedIndex = 1; // Default
        _adaptivePanel.Children.Add(_tuningCombo);

        _targetTempSlider = new Slider { Header = Loc.Form_TargetTemp, Minimum = 50, Maximum = 100, StepFrequency = 1, Value = 85 };
        _adaptivePanel.Children.Add(_targetTempSlider);

        _tdpMaxSlider = new Slider { Header = Loc.Form_TdpMax, Minimum = 6, Maximum = 120, StepFrequency = 1, Value = 55 };
        _adaptivePanel.Children.Add(_tdpMaxSlider);

        _fanMaxSlider = new Slider { Header = Loc.Form_FanMax, Minimum = 0, Maximum = 100, StepFrequency = 1, Value = 100 };
        _adaptivePanel.Children.Add(_fanMaxSlider);

        panel.Children.Add(_adaptivePanel);

        Content = panel;
    }

    private void PopulateFields(Profile? existingProfile)
    {
        if (existingProfile == null)
        {
            // New profile defaults
            _stapmSlider.Value = 45;
            _fastSlider.Value = 50;
            _slowSlider.Value = 45;
            _fanCurveCombo.SelectedItem = _fanCurves.FirstOrDefault(f => f.Builtin) ?? _fanCurves.FirstOrDefault();
            return;
        }

        _nameBox.Text = existingProfile.Name;

        // Set type
        _typeCombo.SelectedIndex = existingProfile.IsAdaptive ? 1 : 0;

        // Set power state
        if (!string.IsNullOrEmpty(existingProfile.PowerState))
        {
            for (int i = 0; i < _powerStateCombo.Items.Count; i++)
            {
                if (_powerStateCombo.Items[i] is ComboBoxItem item && (string?)item.Tag == existingProfile.PowerState)
                {
                    _powerStateCombo.SelectedIndex = i;
                    break;
                }
            }
            // Show the default checkbox and set its state
            _defaultCheckBox.Visibility = Visibility.Visible;
            _defaultCheckBox.IsChecked = existingProfile.IsDefault;
        }

        if (existingProfile.IsAdaptive)
        {
            // Set adaptive fields
            for (int i = 0; i < _tuningCombo.Items.Count; i++)
            {
                if (_tuningCombo.Items[i] is ComboBoxItem item && (string?)item.Tag == existingProfile.Tuning)
                {
                    _tuningCombo.SelectedIndex = i;
                    break;
                }
            }
            _targetTempSlider.Value = existingProfile.TargetTempC;
            _tdpMaxSlider.Value = existingProfile.TdpMaxW;
            _fanMaxSlider.Value = existingProfile.FanMaxPercent;
        }
        else
        {
            // Set fixed fields
            _stapmSlider.Value = existingProfile.Tdp.Stapm;
            _fastSlider.Value = existingProfile.Tdp.Fast;
            _slowSlider.Value = existingProfile.Tdp.Slow;
            _fanCurveCombo.SelectedItem = _fanCurves.FirstOrDefault(f => f.Id == existingProfile.FanCurve)
                ?? _fanCurves.FirstOrDefault(f => f.Builtin)
                ?? _fanCurves.FirstOrDefault();
        }
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        var isAdaptive = _typeCombo.SelectedIndex == 1;
        _fixedPanel.Visibility = isAdaptive ? Visibility.Collapsed : Visibility.Visible;
        _adaptivePanel.Visibility = isAdaptive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPowerStateChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show the "Default" checkbox only when a power state is selected
        var hasPowerState = _powerStateCombo.SelectedItem is ComboBoxItem psItem
            && psItem.Tag is string psTag
            && !string.IsNullOrEmpty(psTag);
        _defaultCheckBox.Visibility = hasPowerState ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            args.Cancel = true;
            return;
        }

        var isAdaptive = _typeCombo.SelectedIndex == 1;

        // Validate fan curve for fixed profiles
        string? fanCurveId = null;
        if (!isAdaptive)
        {
            if (_fanCurveCombo.SelectedItem is not FanCurve selectedCurve)
            {
                args.Cancel = true;
                return;
            }
            fanCurveId = selectedCurve.Id;
        }

        // Get power state
        string? powerState = null;
        if (_powerStateCombo.SelectedItem is ComboBoxItem psItem && psItem.Tag is string psTag && !string.IsNullOrEmpty(psTag))
        {
            powerState = psTag;
        }

        // Get is_default (only meaningful when a power state is assigned)
        bool isDefault = powerState != null && _defaultCheckBox.IsChecked == true;

        if (isAdaptive)
        {
            var tuning = (_tuningCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "default";

            ResultProfile = new Profile
            {
                Id = _editingProfileId ?? "",
                Name = name,
                Type = "adaptive",
                PowerState = powerState,
                IsDefault = isDefault,
                Tuning = tuning,
                TargetTempC = (int)_targetTempSlider.Value,
                TdpMaxW = (int)_tdpMaxSlider.Value,
                FanMaxPercent = (int)_fanMaxSlider.Value,
            };
        }
        else
        {
            ResultProfile = new Profile
            {
                Id = _editingProfileId ?? "",
                Name = name,
                Type = "fixed",
                PowerState = powerState,
                IsDefault = isDefault,
                Tdp = new TdpLimits
                {
                    Stapm = (int)_stapmSlider.Value,
                    Fast = (int)_fastSlider.Value,
                    Slow = (int)_slowSlider.Value,
                },
                FanCurve = fanCurveId!,
            };
        }
    }
}
