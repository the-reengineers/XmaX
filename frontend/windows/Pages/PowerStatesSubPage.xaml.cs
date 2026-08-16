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

        PageTitle.Text = Loc.Title_PowerStateAssignments;

        _profileService = App.ProfileService;
        _profileService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ProfileService.Profiles))
                DispatcherQueue.TryEnqueue(BuildPowerStateUI);
        };

        BuildPowerStateUI();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => App.NavigateBack();

    private void BuildPowerStateUI()
    {
        PowerStateAssignments.Children.Clear();

        var states = new (string Id, string Label)[]
        {
            ("battery", Loc.Power_Battery),
            ("usb_c_slow", Loc.Power_UsbCSlow),
            ("usb_c_fast", Loc.Power_UsbCFast),
            ("dc_in", Loc.Power_DcIn),
        };

        foreach (var (id, label) in states)
        {
            var card = new StackPanel
            {
                Spacing = 8,
                Padding = new Thickness(12),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
            };

            // Header row: power state label + max TDP
            var header = new Grid { ColumnSpacing = 16 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            Grid.SetColumn(labelBlock, 0);
            header.Children.Add(labelBlock);

            var maxTdp = MaxTdpByState[id];
            var tdpBlock = new TextBlock
            {
                Text = $"Max: {maxTdp}W",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(tdpBlock, 1);
            header.Children.Add(tdpBlock);

            card.Children.Add(header);

            // List of profiles assigned to this power state
            var profiles = _profileService.GetProfilesForPowerState(id).ToList();
            if (profiles.Count == 0)
            {
                var noneBlock = new TextBlock
                {
                    Text = Loc.Form_None,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(0, 4, 0, 0),
                };
                card.Children.Add(noneBlock);
            }
            else
            {
                foreach (var profile in profiles)
                {
                    var profileRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 2, 0, 0),
                    };

                    // Default indicator
                    if (profile.IsDefault)
                    {
                        var defaultBadge = new FontIcon
                        {
                            Glyph = "", // Star (filled)
                            FontSize = 14,
                            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        profileRow.Children.Add(defaultBadge);
                    }
                    else
                    {
                        // Spacer to align profile names
                        var spacer = new Border { Width = 18, Height = 14 };
                        profileRow.Children.Add(spacer);
                    }

                    // Profile name + type
                    var profileText = new TextBlock
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    var typeLabel = profile.IsAdaptive ? "Adaptive" : "Fixed";
                    if (profile.IsDefault)
                    {
                        profileText.Text = $"{profile.Name} ({typeLabel}) — Default";
                        profileText.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
                    }
                    else
                    {
                        profileText.Text = $"{profile.Name} ({typeLabel})";
                    }
                    profileRow.Children.Add(profileText);

                    card.Children.Add(profileRow);
                }
            }

            PowerStateAssignments.Children.Add(card);
        }
    }
}
