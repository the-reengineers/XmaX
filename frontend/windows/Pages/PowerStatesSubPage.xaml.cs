using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Pages;

/// <summary>
/// Power states sub-page: read-only display of hardcoded max TDP and assigned profile per power source.
/// Navigated from Settings page drill-down.
/// </summary>
public sealed partial class PowerStatesSubPage : Page
{
    private readonly ProfileService _profileService;

    // Hardcoded max TDP per power state (must match backend power_state_max_tdp)
    private static readonly Dictionary<string, int> MaxTdpByState = new()
    {
        ["battery"] = 55,
        ["usb_c_slow"] = 20,
        ["usb_c_fast"] = 55,
        ["dc_in"] = 80,
    };

    public PowerStatesSubPage()
    {
        this.InitializeComponent();

        _profileService = App.ProfileService;
        _profileService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ProfileService.Profiles))
                DispatcherQueue.TryEnqueue(BuildPowerStateUI);
        };

        BuildPowerStateUI();
    }

    private void BuildPowerStateUI()
    {
        PowerStateAssignments.Children.Clear();

        var states = new (string Id, string Label, string Description)[]
        {
            ("battery", Loc.Power_Battery, "Battery power only"),
            ("usb_c_slow", Loc.Power_UsbCSlow, "Slow USB-C charging (≤65W)"),
            ("usb_c_fast", Loc.Power_UsbCFast, "Fast USB-C charging (≤100W)"),
            ("dc_in", Loc.Power_DcIn, "Dedicated Charger"),
        };

        foreach (var (id, label, description) in states)
        {
            var expander = new SettingsExpander
            {
                Header = label,
                Description = description,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };

            // Max TDP slider
            var maxTdp = MaxTdpByState[id];
            var maxTdpCard = new SettingsCard
            {
                Header = "Max TDP",
                Description = $"{maxTdp} W",
            };

            // Create slider with min/max/current value labels
            var sliderPanel = new Grid
            {
                ColumnSpacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 300,
            };
            sliderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            sliderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sliderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            var minValue = new TextBlock
            {
                Text = "5W",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(minValue, 0);
            sliderPanel.Children.Add(minValue);

            var tdpSlider = new Slider
            {
                Minimum = 5,
                Maximum = maxTdp,
                Value = maxTdp,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = id,
            };
            Grid.SetColumn(tdpSlider, 1);
            sliderPanel.Children.Add(tdpSlider);

            var maxValue = new TextBlock
            {
                Text = $"{maxTdp}W",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(maxValue, 2);
            sliderPanel.Children.Add(maxValue);

            tdpSlider.ValueChanged += (sender, args) =>
            {
                if (sender is Slider slider && slider.Tag is string powerStateId)
                {
                    var newMaxTdp = (int)slider.Value;
                    maxTdpCard.Description = $"{newMaxTdp} W";
                    // TODO: Send to backend to update max TDP for this power state
                    Logger.Debug($"[PowerState] Max TDP changed for {powerStateId}: {newMaxTdp}W");
                }
            };
            maxTdpCard.Content = sliderPanel;
            expander.Items.Add(maxTdpCard);

            // Default profile combobox
            var defaultProfileCard = new SettingsCard
            {
                Header = "Default Profile",
            };
            var profileCombo = new ComboBox
            {
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = id,
            };
            profileCombo.SelectionChanged += OnDefaultProfileChanged;

            // Populate with profiles for this power state
            var profiles = _profileService.GetProfilesForPowerState(id).ToList();
            profileCombo.Items.Add(new ComboBoxItem { Content = Loc.Form_None, Tag = "" });
            foreach (var profile in profiles)
            {
                profileCombo.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
            }

            // Select current default profile
            var currentDefault = profiles.FirstOrDefault(p => p.IsDefault);
            if (currentDefault != null)
            {
                for (int i = 0; i < profileCombo.Items.Count; i++)
                {
                    if (profileCombo.Items[i] is ComboBoxItem item && item.Tag is string profileId && profileId == currentDefault.Id)
                    {
                        profileCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            else
            {
                profileCombo.SelectedIndex = 0; // "None"
            }

            defaultProfileCard.Content = profileCombo;
            expander.Items.Add(defaultProfileCard);

            PowerStateAssignments.Children.Add(expander);
        }
    }

    private async void OnDefaultProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is string powerStateId)
        {
            if (combo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string profileId)
            {
                if (string.IsNullOrEmpty(profileId))
                {
                    // "None" selected
                    Logger.Debug($"[PowerState] Default profile cleared for {powerStateId}");
                }
                else
                {
                    // TODO: Send to backend to set default profile for this power state
                    Logger.Debug($"[PowerState] Default profile changed for {powerStateId}: {profileId}");
                }
            }
        }
    }
}
