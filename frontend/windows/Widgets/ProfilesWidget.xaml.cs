using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Profiles widget showing up to 3 profile tiles. Tap to apply.
/// Active profile is visually highlighted.
/// </summary>
public sealed partial class ProfilesWidget : UserControl, IHomeWidget
{
    /// <summary>Maximum number of profile tiles shown.</summary>
    private const int MaxTiles = 3;

    private readonly ProfileService _profileService;

    public string WidgetId => "profiles";
    public object Control => this;

    public ProfilesWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Profiles;
        _profileService = App.ProfileService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
        RebuildTiles();
    }

    private void OnProfileServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.Profiles) ||
            e.PropertyName == nameof(ProfileService.ActiveProfileId))
        {
            DispatcherQueue.TryEnqueue(RebuildTiles);
        }
    }

    private void RebuildTiles()
    {
        ProfileTiles.Children.Clear();

        var profiles = _profileService.Profiles;
        var activeId = _profileService.ActiveProfileId;

        if (profiles.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = Loc.Empty_NoProfiles,
                Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            ProfileTiles.Children.Add(empty);
            return;
        }

        var count = Math.Min(profiles.Count, MaxTiles);
        for (int i = 0; i < count; i++)
        {
            var profile = profiles[i];
            var isActive = profile.Id == activeId;

            var button = new Button
            {
                Content = profile.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = profile.Id,
            };

            ToolTipService.SetToolTip(button, Loc.F("widget.apply_profile_tooltip", profile.Name));

            if (isActive)
            {
                button.Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["AccentButtonStyle"];
            }

            button.Click += OnProfileTileClick;
            ProfileTiles.Children.Add(button);
        }
    }

    private async void OnProfileTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string profileId)
        {
            try
            {
                await _profileService.ApplyProfileAsync(profileId);
            }
            catch (Exception ex)
            {
                // Show error to user
                await ShowErrorAsync(Loc.Dialog_ApplyFailed, ex.Message);
            }
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Loc.Button_Ok,
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
