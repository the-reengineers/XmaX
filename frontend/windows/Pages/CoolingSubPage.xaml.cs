using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;
using XmaX.ViewModels;

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

    private Grid BuildFanCurveCard(FanCurve curve)
    {
        var grid = new Grid
        {
            Padding = new Thickness(8),
            ColumnSpacing = 8,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = curve.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        info.Children.Add(new TextBlock
        {
            Text = Loc.F("format.points_count", curve.Points.Count),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var editBtn = new Button { Content = Loc.Button_Edit, VerticalAlignment = VerticalAlignment.Center, Tag = curve };
        editBtn.Click += OnEditFanCurveClick;
        Grid.SetColumn(editBtn, 1);
        grid.Children.Add(editBtn);

        var deleteBtn = new Button { Content = Loc.Button_Delete, VerticalAlignment = VerticalAlignment.Center, Tag = curve };
        deleteBtn.Click += OnDeleteFanCurveClick;
        Grid.SetColumn(deleteBtn, 2);
        grid.Children.Add(deleteBtn);

        return grid;
    }

    // ===== Fan Curve CRUD Handlers =====

    private async void OnCreateFanCurveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FanCurveEditorDialog(null) { XamlRoot = this.XamlRoot };
        var result = await dialog.ShowAsync();

        if (dialog.ValidationError != null)
        {
            await ShowErrorAsync(Loc.Dialog_InvalidFanCurve, dialog.ValidationError);
            return;
        }

        if (result == ContentDialogResult.Primary && dialog.ResultCurve != null)
        {
            try
            {
                var c = dialog.ResultCurve;
                await _viewModel.CreateFanCurveAsync(c.Name, c.Points);
            }
            catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_CreateFailed, ex.Message); }
        }
    }

    private async void OnEditFanCurveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FanCurve curve)
        {
            var dialog = new FanCurveEditorDialog(curve) { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();

            if (dialog.ValidationError != null)
            {
                await ShowErrorAsync(Loc.Dialog_InvalidFanCurve, dialog.ValidationError);
                return;
            }

            if (result == ContentDialogResult.Primary && dialog.ResultCurve != null)
            {
                try
                {
                    curve.Name = dialog.ResultCurve.Name;
                    curve.Points = dialog.ResultCurve.Points;
                    await _viewModel.UpdateFanCurveAsync(curve);
                }
                catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_UpdateFailed, ex.Message); }
            }
        }
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
