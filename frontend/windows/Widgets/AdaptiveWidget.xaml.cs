using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Adaptive widget showing adaptive profile cards in a grid layout.
/// Each card represents an adaptive profile. Tap to apply.
/// Active adaptive profile is visually highlighted.
/// </summary>
public sealed partial class AdaptiveWidget : UserControl
{
    private readonly ProfileService _profileService;
    private readonly WidgetService _widgetService;

    public AdaptiveWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Adaptive;
        _profileService = App.ProfileService;
        _widgetService = App.WidgetService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        BuildProfileCards();
        UpdateDisplay();
    }

    private void OnProfileServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.Profiles) ||
            e.PropertyName == nameof(ProfileService.ActiveProfileId) ||
            e.PropertyName == nameof(ProfileService.IsAdaptiveActive))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (e.PropertyName == nameof(ProfileService.Profiles))
                    BuildProfileCards();
                UpdateDisplay();
            });
        }
    }

    private void OnWidgetServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(BuildProfileCards);
        }
    }

    /// <summary>
    /// Build ProfileCards for each adaptive profile in a grid.
    /// </summary>
    private void BuildProfileCards()
    {
        PresetGrid.Children.Clear();
        PresetGrid.ColumnDefinitions.Clear();
        PresetGrid.RowDefinitions.Clear();

        var adaptiveProfiles = _profileService.Profiles
            .Where(p => p.IsAdaptive)
            .ToList();

        if (adaptiveProfiles.Count == 0)
        {
            var placeholder = new TextBlock
            {
                Text = Loc.Info_NoAdaptiveProfiles,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.WrapWholeWords,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            placeholder.SetValue(Grid.RowProperty, 0);
            placeholder.SetValue(Grid.ColumnSpanProperty, Math.Max(1, _widgetService.Columns));
            PresetGrid.Children.Add(placeholder);
            return;
        }

        // Use columns matching widget's actual column span, up to the number of profiles
        var columns = Math.Min(adaptiveProfiles.Count, _widgetService.Columns);

        for (int c = 0; c < columns; c++)
        {
            PresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rows = (adaptiveProfiles.Count + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
        {
            PresetGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < adaptiveProfiles.Count; i++)
        {
            var profile = adaptiveProfiles[i];
            var row = i / columns;
            var col = i % columns;

            var info = $"{profile.Tuning}, {profile.TargetTempC}°C, ≤{profile.TdpMaxW}W";

            var card = new ProfileCard
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name,
                IsAdaptive = true,
                Info = info,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            card.CardTapped += OnProfileCardTapped;
            PresetGrid.Children.Add(card);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
        }
    }

    private void UpdateDisplay()
    {
        var activeId = _profileService.ActiveProfileId;
        var isAdaptive = _profileService.IsAdaptiveActive;

        foreach (var child in PresetGrid.Children)
        {
            if (child is ProfileCard card)
            {
                card.IsSelected = card.ProfileId == activeId && isAdaptive;
            }
        }
    }

    private void OnProfileCardTapped(object? sender, EventArgs e)
    {
        if (sender is ProfileCard card && !string.IsNullOrEmpty(card.ProfileId))
        {
            _ = _profileService.ApplyProfileAsync(card.ProfileId);
        }
    }
}
