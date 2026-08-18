using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Profiles widget showing profile cards in a grid layout.
/// Uses ScrollViewer with mandatory vertical snap points so each row of cards
/// snaps into place — touch, mouse wheel, and kinetic panning all snap row-by-row.
/// </summary>
public sealed partial class ProfilesWidget : UserControl
{
    private readonly ProfileService _profileService;
    private readonly WidgetService _widgetService;
    private bool _isRebuildingRows;
    private double _cardHeight;

    public ProfilesWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Profiles;
        _profileService = App.ProfileService;
        _widgetService = App.WidgetService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        RowsScroller.SizeChanged += (_, _) => RebuildRows();

        RebuildRows();
    }

    private void OnProfileServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.Profiles) ||
            e.PropertyName == nameof(ProfileService.ActiveProfileId))
        {
            DispatcherQueue.TryEnqueue(RebuildRows);
        }
    }

    private void OnWidgetServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(RebuildRows);
        }
    }

    private void RebuildRows()
    {
        if (_isRebuildingRows) return;
        _isRebuildingRows = true;
        try
        {
            DoRebuildRows();
        }
        finally
        {
            _isRebuildingRows = false;
        }
    }

    private double _containerHeight;

    private void DoRebuildRows()
    {
        RowsPanel.Children.Clear();

        var profiles = _profileService.Profiles;
        var columns = _widgetService.Columns;
        if (columns <= 0) columns = 1;

        // Compute card height to match the widget grid's standard row height.
        // cellWidth = standard widget row height (cells are square in the host grid).
        // Each row container has 12px bottom margin (the gap), so:
        //   containerHeight = cellWidth - overhead/N
        //   cardHeight = containerHeight - margin
        var cellWidth = ComputeCellWidth();
        if (cellWidth <= 0) return;

        var gridPadding = RootGrid.Padding.Top + RootGrid.Padding.Bottom;
        var titleGap = RootGrid.RowSpacing;
        var titleHeight = TitleText.ActualHeight;
        var overhead = gridPadding + titleGap + titleHeight;
        var marginSize = 12.0; // Bottom margin on each row container

        // N = widget rowSpan, computed from widget height / cell width.
        int widgetRows = Math.Max(1, (int)Math.Round(RootGrid.ActualHeight / cellWidth));
        _containerHeight = cellWidth - (overhead / widgetRows);
        _cardHeight = _containerHeight - marginSize;
        if (_cardHeight < 0) _cardHeight = 0;
        if (_containerHeight < 0) _containerHeight = 0;

        if (profiles.Count == 0)
        {
            // Show empty state via a single placeholder
            RowsPanel.Children.Add(CreateEmptyStateElement());
            return;
        }

        // Group profiles into rows of `columns` profiles each
        for (int i = 0; i < profiles.Count; i += columns)
        {
            var count = Math.Min(columns, profiles.Count - i);
            var rowProfiles = new List<Profile>(count);
            for (int j = 0; j < count; j++)
            {
                rowProfiles.Add(profiles[i + j]);
            }
            RowsPanel.Children.Add(CreateRowElement(rowProfiles));
        }
    }

    /// <summary>
    /// Create an empty state placeholder element.
    /// </summary>
    private UIElement CreateEmptyStateElement()
    {
        return new TextBlock
        {
            Text = Loc.Empty_NoProfiles,
            Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>
    /// Compute the widget grid cell width (cells are square in the host grid).
    /// </summary>
    private double ComputeCellWidth()
    {
        var columns = _widgetService.Columns;
        var width = RowsScroller.ActualWidth;
        if (width <= 0 || columns <= 0) return 0;
        return width / columns;
    }

    /// <summary>
    /// Build a row panel. Each row is a Grid of ProfileCards.
    /// The ScrollViewer snaps to each row panel as a unit.
    /// </summary>
    private UIElement CreateRowElement(List<Profile> profiles)
    {
        var activeId = _profileService.ActiveProfileId;
        var rowGrid = new Grid
        {
            ColumnSpacing = 8,
        };

        // One column per card in the row (Star sizing distributes width evenly)
        for (int c = 0; c < profiles.Count; c++)
        {
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int c = 0; c < profiles.Count; c++)
        {
            var profile = profiles[c];
            var isActive = profile.Id == activeId;
            var fanCurve = _profileService.FanCurves.FirstOrDefault(f => f.Id == profile.FanCurve);

            var card = new ProfileCard
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name,
                IsAdaptive = profile.IsAdaptive,
                Info = GetProfileInfo(profile),
                FanCurveData = fanCurve,
                IsSelected = isActive,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };

            if (_cardHeight > 0)
            {
                card.Height = _cardHeight;
            }

            card.CardTapped += OnProfileCardTapped;
            rowGrid.Children.Add(card);
            Grid.SetColumn(card, c);
        }

        // Wrap the row in a container with bottom margin for the gap
        var container = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12),
            Height = _containerHeight > 0 ? _containerHeight : double.NaN,
            Children = { rowGrid }
        };

        return container;
    }

    /// <summary>
    /// Get info text for a profile.
    /// </summary>
    private string GetProfileInfo(Profile profile)
    {
        if (profile.IsAdaptive)
        {
            return profile.Tuning.ToUpper();
        }
        return $"{profile.Tdp.Stapm}W · {profile.Tdp.Fast}W · {profile.Tdp.Slow}W";
    }

    private async void OnProfileCardTapped(object? sender, EventArgs e)
    {
        // Walk up the visual tree to find the ProfileCard (tapped element may be a child)
        var card = FindParent<ProfileCard>(sender as DependencyObject);
        if (card != null && !string.IsNullOrEmpty(card.ProfileId))
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

    /// <summary>
    /// Walk up the visual tree to find the nearest parent of type T.
    /// </summary>
    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            if (current is T match) return match;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
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

