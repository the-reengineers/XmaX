using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Profiles widget showing profile cards in a grid layout.
/// Each card uses 1 column width matching the home page column count.
/// Uses ProfileCard component for each profile. Tap to apply.
/// Active profile is visually highlighted.
/// </summary>
public sealed partial class ProfilesWidget : UserControl, IHomeWidget
{
    private readonly ProfileService _profileService;
    private readonly WidgetService _widgetService;

    public string WidgetId => "profiles";
    public WidgetConfig Config => WidgetConfig.FlexibleTransparent(1, 4);  // Flexible, transparent
    public object Control => this;

    public ProfilesWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Profiles;
        _profileService = App.ProfileService;
        _widgetService = App.WidgetService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        RebuildCards();
    }

    private void OnProfileServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.Profiles) ||
            e.PropertyName == nameof(ProfileService.ActiveProfileId))
        {
            DispatcherQueue.TryEnqueue(RebuildCards);
        }
    }

    private void OnWidgetServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(RebuildCards);
        }
    }

    private void RebuildCards()
    {
        CardsGrid.Children.Clear();
        CardsGrid.ColumnDefinitions.Clear();
        CardsGrid.RowDefinitions.Clear();

        var profiles = _profileService.Profiles;
        var activeId = _profileService.ActiveProfileId;
        // Use the widget's actual column span (min of MaxColumns and home page columns)
        var columns = Math.Min(Config.MaxColumns, _widgetService.Columns);

        if (profiles.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = Loc.Empty_NoProfiles,
                Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            CardsGrid.Children.Add(empty);
            return;
        }

        // Create columns matching home page column count
        for (int c = 0; c < columns; c++)
        {
            CardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Calculate rows needed
        var rows = (profiles.Count + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
        {
            CardsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        // Add profile cards
        for (int i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var isActive = profile.Id == activeId;
            var row = i / columns;
            var col = i % columns;

            var card = new ProfileCard
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name,
                Info = GetProfileInfo(profile),
                IsSelected = isActive,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            card.CardTapped += OnProfileCardTapped;
            CardsGrid.Children.Add(card);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
        }
    }

    /// <summary>
    /// Get info text for a profile (e.g., TDP limits, fan curve).
    /// </summary>
    private string GetProfileInfo(Profile profile)
    {
        var parts = new List<string>();
        if (profile.Tdp.Stapm > 0)
            parts.Add($"{profile.Tdp.Stapm}W");
        if (!string.IsNullOrEmpty(profile.FanCurve))
            parts.Add(profile.FanCurve);
        return parts.Count > 0 ? string.Join(" · ", parts) : "";
    }

    private async void OnProfileCardTapped(object? sender, EventArgs e)
    {
        if (sender is ProfileCard card && !string.IsNullOrEmpty(card.ProfileId))
        {
            try
            {
                await _profileService.ApplyProfileAsync(card.ProfileId);
            }
            catch (Exception ex)
            {
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
