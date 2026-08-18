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
public sealed partial class ProfilesWidget : UserControl
{
    private readonly ProfileService _profileService;
    private readonly WidgetService _widgetService;
    private bool _isRebuildingCards;
    private bool _isSnapping;
    private double _scrollStartOffset = -1;
    private double _lastScrollDirection; // positive = down, negative = up
    private bool _wheelScrollPending;
    private double _cardHeight;
    private bool _isTouch;

    public ProfilesWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Profiles;
        _profileService = App.ProfileService;
        _widgetService = App.WidgetService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        CardsScroller.SizeChanged += (_, _) => RebuildCards();
        // Register on the ScrollViewer with handledEventsToo so our handler fires
        // even if the ScrollViewer's internal wheel handler fires first.
        CardsScroller.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnCardsPointerWheelChanged),
            handledEventsToo: true);
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
        // Guard against reentrancy from SizeChanged triggered by our own layout changes
        if (_isRebuildingCards) return;
        _isRebuildingCards = true;
        try
        {
            DoRebuildCards();
        }
        finally
        {
            _isRebuildingCards = false;
        }
    }

    private void DoRebuildCards()
    {
        CardsGrid.Children.Clear();
        CardsGrid.ColumnDefinitions.Clear();
        CardsGrid.RowDefinitions.Clear();

        var profiles = _profileService.Profiles;
        var activeId = _profileService.ActiveProfileId;
        // Always fill the full row width
        var columns = _widgetService.Columns;

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

        // Calculate rows needed
        var rows = (profiles.Count + columns - 1) / columns;

        // Card height = standard widget row height (= column width, since host grid
        // cells are square) minus all vertical overhead distributed across N rows.
        // N = the number of widget-grid rows the widget spans (its rowSpan).
        //
        // Vertical overhead that reduces available scroll area:
        //   - gridPadding: RootGrid Padding top+bottom
        //   - titleGap: RootGrid RowSpacing between title row and scroll area
        //   - interCardSpacing: CardsGrid RowSpacing between card rows ((N-1) gaps for N cards)
        //   - titleHeight: rendered title text height
        //
        // scrollarea = N*cellWidth - gridPadding - titleGap - titleHeight
        // content = N*cardHeight + (N-1)*interCardSpacing
        // For exact fit: cardHeight = (scrollarea - (N-1)*interCardSpacing) / N
        //                          = cellWidth - (gridPadding + titleGap + titleHeight) / N
        //                            - (N-1) * interCardSpacing / N
        var cellWidth = ComputeCellWidth();
        var gridPadding = RootGrid.Padding.Top + RootGrid.Padding.Bottom;
        var titleGap = RootGrid.RowSpacing;
        var interCardSpacing = CardsGrid.RowSpacing;
        var titleHeight = TitleText.ActualHeight;

        // Compute N (widget rowSpan) from the widget's actual height / cell width.
        // The widget container is sized as rowSpan * cellHeight by the host grid.
        int widgetRows = 1;
        if (cellWidth > 0)
        {
            widgetRows = Math.Max(1, (int)Math.Round(RootGrid.ActualHeight / cellWidth));
        }

        var titleOverhead = gridPadding + titleGap + titleHeight;
        _cardHeight = cellWidth
                    - (widgetRows > 0 ? titleOverhead / widgetRows : 0)
                    - (widgetRows > 1 ? (widgetRows - 1) * interCardSpacing / widgetRows : 0);
        var cardHeight = _cardHeight;
        if (cardHeight < 0) cardHeight = 0;

        // Create columns matching home page column count
        for (int c = 0; c < columns; c++)
        {
            CardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int r = 0; r < rows; r++)
        {
            CardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // Add profile cards
        for (int i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var isActive = profile.Id == activeId;
            var row = i / columns;
            var col = i % columns;

            // Resolve fan curve data for mini chart
            var fanCurve = _profileService.FanCurves.FirstOrDefault(f => f.Id == profile.FanCurve);

            var card = new ProfileCard
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name,
                IsAdaptive = profile.IsAdaptive,
                Info = GetProfileInfo(profile),
                FanCurveData = fanCurve,
                IsSelected = isActive,
            };

            if (cardHeight > 0)
            {
                card.Height = cardHeight;
            }

            card.CardTapped += OnProfileCardTapped;
            CardsGrid.Children.Add(card);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
        }
    }

    /// <summary>
    /// Get info text for a profile.
    /// Fixed: TDP values. Adaptive: tuning type uppercase.
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

    // ===== Cell sizing =====

    /// <summary>
    /// Compute the widget grid cell width (== standard widget row height for square cells).
    /// cellWidth = (CardsGrid width - (columns-1) * spacing) / columns.
    /// </summary>
    private double ComputeCellWidth()
    {
        var columns = _widgetService.Columns;
        var gridWidth = CardsGrid.ActualWidth;
        if (gridWidth <= 0 || columns <= 0) return 0;
        var w = (gridWidth - (columns - 1) * 8.0) / columns;
        return w > 0 ? w : 0;
    }

    // ===== Snap scrolling (mirrors WidgetGridHost snap logic) =====

    /// <summary>
    /// Pointer wheel: record scroll direction for snap.
    /// Wheel events only fire for mouse/touchpad (never touch), so this
    /// also clears the _isTouch flag to re-enable snap.
    /// </summary>
    private void OnCardsPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isTouch = false;

        var props = e.GetCurrentPoint(CardsScroller).Properties;
        var mouseWheelDelta = props.MouseWheelDelta;
        if (mouseWheelDelta == 0) return;

        // Positive delta = wheel forward = scroll UP (content moves down)
        _lastScrollDirection = mouseWheelDelta > 0 ? -1.0 : 1.0;
        _wheelScrollPending = true;
    }

    /// <summary>
    /// ViewChanged: snap to card-height multiples. Wheel uses boundary stepping;
    /// non-wheel tracks direction and snaps on settle.
    /// Touch input skips snap entirely for natural free-form scrolling.
    /// </summary>
    private void OnCardsScrollerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // Touch input: let ScrollViewer handle scrolling naturally (no snap)
        if (_isTouch) return;

        if (_isSnapping)
        {
            if (!e.IsIntermediate) _isSnapping = false;
            return;
        }

        var rowUnit = _cardHeight + 8.0; // card height + RowSpacing
        if (rowUnit <= 0) return;

        if (e.IsIntermediate)
        {
            if (_wheelScrollPending)
            {
                var currentOffset = CardsScroller.VerticalOffset;
                var snapOffset = ComputeCardsSnapTarget(currentOffset, _lastScrollDirection, rowUnit);

                var maxOffset = Math.Max(0, CardsScroller.ExtentHeight - CardsScroller.ViewportHeight);
                snapOffset = Math.Clamp(snapOffset, 0, maxOffset);

                if (Math.Abs(currentOffset - snapOffset) > 0.5)
                {
                    _isSnapping = true;
                    CardsScroller.ChangeView(null, snapOffset, null, disableAnimation: false);
                }
                return;
            }

            // Non-wheel: just track scroll direction
            var currentOffset2 = CardsScroller.VerticalOffset;
            if (_scrollStartOffset < 0)
                _scrollStartOffset = currentOffset2;
            _lastScrollDirection = currentOffset2 - _scrollStartOffset;
            return;
        }

        // Final event (scroll settled)

        if (_wheelScrollPending)
        {
            _wheelScrollPending = false;
            var settledOffset = CardsScroller.VerticalOffset;
            var snapOffset = ComputeCardsSnapTarget(settledOffset, _lastScrollDirection, rowUnit);

            var maxOffset = Math.Max(0, CardsScroller.ExtentHeight - CardsScroller.ViewportHeight);
            snapOffset = Math.Clamp(snapOffset, 0, maxOffset);

            if (Math.Abs(settledOffset - snapOffset) > 1.0)
            {
                _isSnapping = true;
                CardsScroller.ChangeView(null, snapOffset, null, disableAnimation: false);
            }
            return;
        }

        // Non-wheel scroll settled — use tracked direction
        var finalOffset = CardsScroller.VerticalOffset;
        var startOffset = _scrollStartOffset >= 0 ? _scrollStartOffset : finalOffset;
        _scrollStartOffset = -1;

        var snap = ComputeCardsSnapTarget(finalOffset, finalOffset - startOffset, rowUnit);
        var maxOff = Math.Max(0, CardsScroller.ExtentHeight - CardsScroller.ViewportHeight);
        snap = Math.Clamp(snap, 0, maxOff);

        if (Math.Abs(finalOffset - snap) > 1.0)
        {
            _isSnapping = true;
            CardsScroller.ChangeView(null, snap, null, disableAnimation: false);
        }
    }

    /// <summary>
    /// Manipulation started: mark this interaction as touch-based.
    /// Subsequent ViewChanged and InertiaStarting events will skip snap.
    /// </summary>
    private void OnCardsScrollerManipulationStarted(
        object sender,
        Microsoft.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e)
    {
        _isTouch = true;
    }

    /// <summary>
    /// Manipulation completed: reset touch tracking for the next interaction.
    /// </summary>
    private void OnCardsScrollerManipulationCompleted(
        object sender,
        Microsoft.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e)
    {
        _isTouch = false;
    }

    /// <summary>
    /// Manipulation inertia ending: snap to nearest card boundary in the
    /// direction of velocity (falling back to tracked scroll direction).
    /// Touch input skips snap — let natural inertia decelerate freely.
    /// </summary>
    private void OnCardsScrollerInertiaStarting(
        object sender,
        Microsoft.UI.Xaml.Input.ManipulationInertiaStartingRoutedEventArgs e)
    {
        // Touch input: no snap, let inertia decelerate naturally
        if (_isTouch) return;

        if (_isSnapping) return;

        var currentOffset = CardsScroller.VerticalOffset;
        var rowUnit = _cardHeight + 8.0;
        if (rowUnit <= 0) return;

        var velocity = e.Velocities.Linear.Y;
        double direction;
        if (Math.Abs(velocity) > 0.01)
            direction = velocity; // positive = scrolling down (content moves up)
        else
            direction = _lastScrollDirection;

        var snapOffset = ComputeCardsSnapTarget(currentOffset, direction, rowUnit);

        var maxOffset = Math.Max(0, CardsScroller.ExtentHeight - CardsScroller.ViewportHeight);
        snapOffset = Math.Clamp(snapOffset, 0, maxOffset);

        if (Math.Abs(currentOffset - snapOffset) > 1.0)
        {
            _isSnapping = true;
            _scrollStartOffset = -1;
            _lastScrollDirection = 0;

            // Cancel default inertia and start our own animated scroll
            e.Handled = true;
            CardsScroller.ChangeView(null, snapOffset, null, disableAnimation: false);
        }
    }

    /// <summary>
    /// Compute the snap target for a given offset and direction.
    /// Uses tolerance-based rounding to detect exact row boundaries, and
    /// steps one row in the scroll direction before applying ceiling/floor.
    /// </summary>
    private static double ComputeCardsSnapTarget(double currentOffset, double direction, double rowUnit)
    {
        var norm = currentOffset / rowUnit;
        var rounded = Math.Round(norm);

        // If on an exact boundary (within 2% tolerance), step one row in the
        // scroll direction first — then ceiling/floor lands on the next boundary.
        if (Math.Abs(norm - rounded) < 0.02)
        {
            if (direction < -0.5)
                return (rounded - 1) * rowUnit;
            if (direction > 0.5)
                return (rounded + 1) * rowUnit;
            return rounded * rowUnit;
        }

        // Not on boundary — ceiling/floor gets the next boundary in scroll direction
        if (direction > 0)
            return Math.Ceiling(norm) * rowUnit;
        if (direction < 0)
            return Math.Floor(norm) * rowUnit;

        // No direction — snap to nearest
        return rounded * rowUnit;
    }
}
