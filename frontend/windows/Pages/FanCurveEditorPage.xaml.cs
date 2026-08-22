using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using XmaX.Models;
using XmaX.Services;
using XmaX.Widgets;

namespace XmaX.Pages;

/// <summary>
/// Page for creating or editing a fan curve.
/// Navigated from CoolingSubPage via SettingsPage breadcrumb navigation.
/// Saves directly to ProfileService — caller refreshes on return.
/// </summary>
public sealed partial class FanCurveEditorPage : Page
{
    private readonly ObservableCollection<FanCurvePoint> _points = new();
    private FanCurve? _existingCurve;
    private bool _isNew;
    private readonly ProfileService _profileService;
    private bool _isDirty;
    private bool _isLoading;

    public bool IsDirty => _isDirty;

    public void MarkAsClean() => _isDirty = false;

    public FanCurveEditorPage()
    {
        this.InitializeComponent();
        _profileService = App.ProfileService;

        // Localize UI
        NameBox.Header = Loc.Form_Name;
        CancelButton.Content = Loc.Button_Cancel;
        SaveButton.Content = Loc.Button_Save;

        // Dirty tracking
        NameBox.TextChanged += MarkDirty;
        _points.CollectionChanged += MarkDirty;
        Graph.PointChanged += MarkDirty;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _existingCurve = e.Parameter as FanCurve;
        _isNew = _existingCurve == null;

        _isLoading = true;

        if (_existingCurve != null)
        {
            // Edit mode
            NameBox.Text = _existingCurve.Name;
            NameBox.PlaceholderText = Loc.Form_FanCurveName;
            foreach (var point in _existingCurve.Points)
            {
                _points.Add(new FanCurvePoint { TempC = point.TempC, SpeedPercent = point.SpeedPercent });
            }
        }
        else
        {
            // New mode — default points
            NameBox.PlaceholderText = Loc.Form_FanCurveName;
            _points.Add(new FanCurvePoint { TempC = 40, SpeedPercent = 20 });
            _points.Add(new FanCurvePoint { TempC = 60, SpeedPercent = 40 });
            _points.Add(new FanCurvePoint { TempC = 80, SpeedPercent = 80 });
        }

        Graph.Points = _points;
        _isLoading = false;
        _isDirty = false;
    }

    /// <summary>
    /// Get the page title for breadcrumb navigation.
    /// </summary>
    public string GetPageTitle()
    {
        if (_isNew)
        {
            return Loc.Dialog_CreateFanCurve;
        }
        return _existingCurve?.Name ?? Loc.Dialog_EditFanCurve;
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            if (!await ShowUnsavedChangesDialogAsync())
                return;
        }
        if (Frame.CanGoBack)
        {
            Frame.GoBack(new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
        }
    }

    private void MarkDirty(object? sender, object e)
    {
        if (!_isLoading) _isDirty = true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowNameError();
            return;
        }

        // Validate points
        var sortedPoints = _points.OrderBy(p => p.TempC).ToList();
        if (!XmaX.ViewModels.CoolingViewModel.ValidateFanCurvePoints(sortedPoints, out var error))
        {
            ShowValidationError(error!);
            return;
        }

        try
        {
            var curve = new FanCurve
            {
                Id = _existingCurve?.Id ?? "",
                Name = name,
                Points = sortedPoints,
            };
            await _profileService.SaveFanCurveAsync(curve);

            // Navigate back — CoolingSubPage will refresh via ViewModel.PropertyChanged
            if (Frame.CanGoBack)
            {
                Frame.GoBack(new SlideNavigationTransitionInfo
                {
                    Effect = SlideNavigationTransitionEffect.FromRight
                });
            }
        }
        catch (Exception ex)
        {
            ShowValidationError(ex.Message);
        }
    }

    private void ShowNameError()
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Dialog_InvalidFanCurve,
            Content = Loc.Form_FanCurveName,
            CloseButtonText = Loc.Button_Ok,
            XamlRoot = this.XamlRoot,
        };
        _ = dialog.ShowAsync();
    }

    private void ShowValidationError(string error)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Dialog_InvalidFanCurve,
            Content = error,
            CloseButtonText = Loc.Button_Ok,
            XamlRoot = this.XamlRoot,
        };
        _ = dialog.ShowAsync();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        if (_isDirty)
        {
            e.Cancel = true;
            _ = ShowUnsavedChangesDialogAsync().ContinueWith(task =>
            {
                if (task.Result)
                {
                    _isDirty = false;
                    Frame.GoBack();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            base.OnNavigatingFrom(e);
        }
    }

    private async Task<bool> ShowUnsavedChangesDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Dialog_UnsavedChanges,
            Content = Loc.Dialog_UnsavedChangesMessage,
            PrimaryButtonText = Loc.Button_Discard,
            CloseButtonText = Loc.Button_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        App.MainWindow?.SetModalDialogOpen(true);
        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        finally
        {
            App.MainWindow?.SetModalDialogOpen(false);
        }
    }
}
