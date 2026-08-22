using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;
using XmaX.ViewModels;
using XmaX.Widgets;

namespace XmaX.Pages;

/// <summary>
/// Cooling sub-page: fan curve CRUD.
/// Navigated from Settings page drill-down.
/// </summary>
public sealed partial class CoolingSubPage : Page
{
    private readonly CoolingViewModel _viewModel;

    public CoolingSubPage()
    {
        this.InitializeComponent();

        CreateFanCurveBtn.Content = Loc.Button_CreateFanCurve;

        _viewModel = new CoolingViewModel(App.ProfileService);
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(CoolingViewModel.FanCurves))
                DispatcherQueue.TryEnqueue(() => RebuildFanCurvesList());
        };

        RebuildFanCurvesList();
    }

    // ===== Fan Curves List =====

    private void RebuildFanCurvesList()
    {
        FanCurvesList.ItemsSource = null;
        var panel = new StackPanel { Spacing = 6 };

        if (_viewModel.FanCurves.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Empty_NoFanCurvesHint,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        else
        {
            foreach (var curve in _viewModel.FanCurves)
            {
                panel.Children.Add(BuildFanCurveCard(curve));
            }
        }

        FanCurvesList.ItemsSource = new List<UIElement> { panel };
        FanCurvesList.ItemTemplate = null;
    }

    private SettingsExpander BuildFanCurveCard(FanCurve curve)
    {
        var expander = new SettingsExpander
        {
            Header = curve.Name,
            Description = Loc.F("format.points_count", curve.Points.Count),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        // Fan curve visual
        var chartCard = new SettingsCard
        {
            Header = "Preview",
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var chart = new MiniFanCurveChart
        {
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
            ShowAxis = false,
            GridlineInterval = 10,
        };
        chart.SetCurve(curve);
        chartCard.Content = chart;
        expander.Items.Add(chartCard);

        // Edit button
        var editCard = new SettingsCard
        {
            Header = "Edit this fan curve",
        };
        var editBtn = new Button
        {
            Content = Loc.Button_Edit,
            Tag = curve,
        };
        editBtn.Click += OnEditFanCurveClick;
        editCard.Content = editBtn;
        expander.Items.Add(editCard);

        // Remove button
        var removeCard = new SettingsCard
        {
            Header = "Remove this fan curve",
        };
        var removeBtn = new Button
        {
            Content = "Remove",
            Tag = curve,
        };
        removeBtn.Click += OnDeleteFanCurveClick;
        removeCard.Content = removeBtn;
        expander.Items.Add(removeCard);

        return expander;
    }

    // ===== Fan Curve CRUD Handlers =====

    private void OnCreateFanCurveClick(object sender, RoutedEventArgs e)
    {
        // Navigate to editor page for new fan curve
        var settingsPage = GetParentSettingsPage();
        settingsPage?.NavigateToSubPage(typeof(FanCurveEditorPage), parameter: null);
    }

    private void OnEditFanCurveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FanCurve curve)
        {
            // Navigate to editor page with the existing curve
            var settingsPage = GetParentSettingsPage();
            settingsPage?.NavigateToSubPage(typeof(FanCurveEditorPage), parameter: curve);
        }
    }

    private SettingsPage? GetParentSettingsPage()
    {
        // Walk up the visual tree to find the parent SettingsPage
        DependencyObject current = this;
        while (current != null)
        {
            if (current is SettingsPage page)
                return page;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async void OnDeleteFanCurveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FanCurve curve)
        {
            try
            {
                await _viewModel.DeleteFanCurveAsync(curve.Id);
            }
            catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_DeleteFailed, ex.Message); }
        }
    }

    // ===== Helpers =====

    private async Task ShowErrorAsync(string title, string message)
    {
        if (this.XamlRoot == null) return;

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
