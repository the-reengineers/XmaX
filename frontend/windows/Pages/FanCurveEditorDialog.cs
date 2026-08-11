using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;
using XmaX.Widgets;

namespace XmaX.Pages;

/// <summary>
/// Dialog for creating or editing a fan curve.
/// Uses a visual graph with draggable points.
/// </summary>
public sealed class FanCurveEditorDialog : ContentDialog
{
    private readonly bool _isEdit;
    private readonly ObservableCollection<FanCurvePoint> _points = new();

    private TextBox _nameBox = null!;
    private FanCurveGraph _graph = null!;

    /// <summary>The resulting fan curve after OK, or null if cancelled.</summary>
    public FanCurve? ResultCurve { get; private set; }

    public FanCurveEditorDialog(FanCurve? existingCurve)
    {
        _isEdit = existingCurve != null;

        Title = _isEdit ? Loc.Dialog_EditFanCurve : Loc.Dialog_CreateFanCurve;
        PrimaryButtonText = _isEdit ? Loc.Button_Save : Loc.Button_Create;
        CloseButtonText = Loc.Button_Cancel;
        DefaultButton = ContentDialogButton.Primary;

        InitializeContent();
        PrimaryButtonClick += OnPrimaryButtonClick;

        // Populate fields
        if (existingCurve != null)
        {
            _nameBox.Text = existingCurve.Name;
            foreach (var point in existingCurve.Points)
            {
                _points.Add(new FanCurvePoint { TempC = point.TempC, SpeedPercent = point.SpeedPercent });
            }
        }
        else
        {
            // Default points
            _points.Add(new FanCurvePoint { TempC = 40, SpeedPercent = 20 });
            _points.Add(new FanCurvePoint { TempC = 60, SpeedPercent = 40 });
            _points.Add(new FanCurvePoint { TempC = 80, SpeedPercent = 80 });
        }
    }

    private void InitializeContent()
    {
        var mainPanel = new StackPanel { Spacing = 12, MinWidth = 400 };

        _nameBox = new TextBox { Header = Loc.Form_Name, PlaceholderText = Loc.Form_FanCurveName };
        mainPanel.Children.Add(_nameBox);

        // Graph only
        _graph = new FanCurveGraph
        {
            Width = 350,
            Height = 350,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _graph.Points = _points;
        mainPanel.Children.Add(_graph);

        Content = mainPanel;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            args.Cancel = true;
            return;
        }

        // Validate points
        var sortedPoints = _points.OrderBy(p => p.TempC).ToList();
        if (!XmaX.ViewModels.CoolingViewModel.ValidateFanCurvePoints(sortedPoints, out var error))
        {
            args.Cancel = true;
            var errDialog = new ContentDialog
            {
                Title = Loc.Dialog_InvalidFanCurve,
                Content = error,
                CloseButtonText = Loc.Button_Ok,
                XamlRoot = this.XamlRoot,
            };
            _ = errDialog.ShowAsync();
            return;
        }

        ResultCurve = new FanCurve
        {
            Name = name,
            Points = sortedPoints,
        };
    }
}
