using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;

namespace XmaX.Pages;

/// <summary>
/// Dialog for creating or editing a fan curve.
/// Uses a simple list-based point editor (add/remove points with temp/speed fields).
/// </summary>
public sealed class FanCurveEditorDialog : ContentDialog
{
    private readonly bool _isEdit;
    private readonly List<FanCurvePoint> _points = new();

    private TextBox _nameBox = null!;
    private StackPanel _pointsPanel = null!;

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

        RebuildPointsList();
    }

    private void InitializeContent()
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 350 };

        _nameBox = new TextBox { Header = Loc.Form_Name, PlaceholderText = Loc.Form_FanCurveName };
        panel.Children.Add(_nameBox);

        // Points header
        var pointsHeader = new Grid();
        pointsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pointsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pointsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tempHeader = new TextBlock { Text = Loc.Form_Temp, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tempHeader, 0);
        pointsHeader.Children.Add(tempHeader);

        var speedHeader = new TextBlock { Text = Loc.Form_Speed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(speedHeader, 1);
        pointsHeader.Children.Add(speedHeader);

        panel.Children.Add(pointsHeader);

        // Points list
        _pointsPanel = new StackPanel { Spacing = 4 };
        panel.Children.Add(_pointsPanel);

        // Add point button
        var addBtn = new Button { Content = Loc.Button_AddPoint, HorizontalAlignment = HorizontalAlignment.Left };
        addBtn.Click += (s, e) => AddPoint();
        panel.Children.Add(addBtn);

        Content = panel;
    }

    private void RebuildPointsList()
    {
        _pointsPanel.Children.Clear();

        foreach (var point in _points.OrderBy(p => p.TempC))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tempBox = new NumberBox
            {
                Value = point.TempC,
                Minimum = 0,
                Maximum = 120,
                SmallChange = 1,
                LargeChange = 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Tag = point,
            };
            tempBox.ValueChanged += (s, e) => point.TempC = (int)e.NewValue;
            Grid.SetColumn(tempBox, 0);
            row.Children.Add(tempBox);

            var speedBox = new NumberBox
            {
                Value = point.SpeedPercent,
                Minimum = 0,
                Maximum = 100,
                SmallChange = 1,
                LargeChange = 5,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = point,
            };
            speedBox.ValueChanged += (s, e) => point.SpeedPercent = (int)e.NewValue;
            Grid.SetColumn(speedBox, 1);
            row.Children.Add(speedBox);

            var removeBtn = new Button { Content = "✕", Tag = point, Padding = new Thickness(8, 4, 8, 4) };
            removeBtn.Click += (s, e) => RemovePoint(point);
            Grid.SetColumn(removeBtn, 2);
            row.Children.Add(removeBtn);

            _pointsPanel.Children.Add(row);
        }
    }

    private void AddPoint()
    {
        var lastTemp = _points.Count > 0 ? _points.Max(p => p.TempC) : 40;
        _points.Add(new FanCurvePoint { TempC = lastTemp + 10, SpeedPercent = 50 });
        RebuildPointsList();
    }

    private void RemovePoint(FanCurvePoint point)
    {
        _points.Remove(point);
        RebuildPointsList();
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
        if (!XmaX.ViewModels.ProfilesViewModel.ValidateFanCurvePoints(sortedPoints, out var error))
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
