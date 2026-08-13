using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Adaptive widget showing tuning preset cards in a grid layout.
/// Each card uses 1 column width matching the home page column count.
/// Uses ProfileCard component for each preset. Tap to apply.
/// Active preset is visually highlighted.
/// </summary>
public sealed partial class AdaptiveWidget : UserControl, IHomeWidget
{
    private readonly AutoTuneService _autoTuneService;
    private readonly WidgetService _widgetService;

    public string WidgetId => "adaptive";
    public WidgetConfig Config => WidgetConfig.FixedTransparent(2, 3);  // 2-3 cols, transparent
    public object Control => this;
    public string? Title => Loc.Title_Adaptive;
    public int GetRequiredRows(int availableColumns) => Config.Rows;

    public AdaptiveWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Adaptive;
        _autoTuneService = App.AutoTuneService;
        _widgetService = App.WidgetService;
        _autoTuneService.PropertyChanged += OnAutoTuneChanged;
        _widgetService.PropertyChanged += OnWidgetServiceChanged;
        BuildPresetCards();
        UpdateDisplay();
    }

    private void OnAutoTuneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoTuneService.State) ||
            e.PropertyName == nameof(AutoTuneService.IsActive) ||
            e.PropertyName == nameof(AutoTuneService.Tuning))
        {
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void OnWidgetServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            DispatcherQueue.TryEnqueue(BuildPresetCards);
        }
    }

    /// <summary>
    /// Build ProfileCards for each tuning preset (silent, default, performance) in a grid.
    /// Each card uses 1 column width matching the widget's actual column span.
    /// </summary>
    private void BuildPresetCards()
    {
        PresetGrid.Children.Clear();
        PresetGrid.ColumnDefinitions.Clear();
        PresetGrid.RowDefinitions.Clear();

        var presets = new[]
        {
            new { Tuning = "silent", Name = Loc.Button_Silent, Info = "60°C" },
            new { Tuning = "default", Name = Loc.Button_Default, Info = "80°C" },
            new { Tuning = "performance", Name = Loc.Button_Performance, Info = "95°C" },
        };

        // Use the widget's actual column span (min of MaxColumns and home page columns)
        var columns = Math.Min(Config.MaxColumns, _widgetService.Columns);

        // Create columns matching the widget's column span
        for (int c = 0; c < columns; c++)
        {
            PresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        // Calculate rows needed
        var rows = (presets.Length + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
        {
            PresetGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        // Add preset cards
        for (int i = 0; i < presets.Length; i++)
        {
            var preset = presets[i];
            var row = i / columns;
            var col = i % columns;

            var card = new ProfileCard
            {
                ProfileId = preset.Tuning,
                DisplayName = preset.Name,
                Info = preset.Info,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            card.CardTapped += OnPresetCardTapped;
            PresetGrid.Children.Add(card);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
        }
    }

    private void UpdateDisplay()
    {
        var tuning = _autoTuneService.Tuning;
        var isActive = _autoTuneService.IsActive;

        // Update card selection state
        foreach (var child in PresetGrid.Children)
        {
            if (child is ProfileCard card)
            {
                card.IsSelected = card.ProfileId == tuning && isActive;
            }
        }
    }

    private void OnPresetCardTapped(object? sender, EventArgs e)
    {
        if (sender is ProfileCard card && !string.IsNullOrEmpty(card.ProfileId))
        {
            var state = _autoTuneService.State;
            _ = _autoTuneService.SetAutoTuneAsync(
                card.ProfileId,
                state.TargetTempC,
                state.TdpMaxW,
                state.FanMaxPercent);
        }
    }
}
