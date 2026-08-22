using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;

namespace XmaX.Widgets;

/// <summary>
/// A toggleable card component for displaying profiles.
/// Used by ProfilesWidget and AdaptiveWidget.
/// </summary>
public sealed partial class ProfileCard : UserControl
{
    private bool _isSelected;

    public ProfileCard()
    {
        this.InitializeComponent();
        UpdateVisualState();
    }

    /// <summary>Profile ID/slug.</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>Profile display name.</summary>
    public string DisplayName
    {
        get => NameText.Text;
        set => NameText.Text = value;
    }

    /// <summary>Whether this is an adaptive profile (controls the type icon).</summary>
    public bool IsAdaptive
    {
        set => TypeIcon.Text = value ? "\U0000EFFE" : "\U0000EDDE";
    }

    /// <summary>Additional info text (e.g., temp target for adaptive profiles).</summary>
    public string Info
    {
        get => InfoText.Text;
        set => InfoText.Text = value;
    }

    /// <summary>TDP limits to display stacked in the left column. If null, shows Info text instead.</summary>
    public XmaX.Models.TdpLimits? TdpValues
    {
        set => PopulateTdpPanel(value);
    }

    private void PopulateTdpPanel(XmaX.Models.TdpLimits? tdp)
    {
        TdpPanel.Children.Clear();
        if (tdp == null)
        {
            TdpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TdpPanel.Visibility = Visibility.Visible;

        TdpPanel.Children.Add(MakeTdpValue($"{tdp.Stapm}W"));
        TdpPanel.Children.Add(MakeTdpValue($"{tdp.Fast}W"));
        TdpPanel.Children.Add(MakeTdpValue($"{tdp.Slow}W"));
    }

    private static TextBlock MakeTdpValue(string value)
    {
        return new TextBlock
        {
            Text = value,
            Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
    }

    /// <summary>Fan curve data for the mini chart at the bottom of the card.</summary>
    public FanCurve? FanCurveData
    {
        set => FanCurveChart.SetCurve(value);
    }

    /// <summary>Whether this card is selected/active.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            UpdateVisualState();
            SelectionChanged?.Invoke(this, value);
        }
    }

    /// <summary>Raised when selection state changes.</summary>
    public event EventHandler<bool>? SelectionChanged;

    /// <summary>Raised when card is tapped/clicked.</summary>
    public event EventHandler? CardTapped;

    private void UpdateVisualState()
    {
        if (_isSelected)
        {
            // Selected state: accent border
            RootBorder.BorderBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
            RootBorder.BorderThickness = new Thickness(2);
        }
        else
        {
            // Normal state: no border
            RootBorder.BorderBrush = null;
            RootBorder.BorderThickness = new Thickness(0);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Hover effect
        RootBorder.Background = Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"] as Brush;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Restore normal background
        RootBorder.Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        IsSelected = true;
        CardTapped?.Invoke(this, EventArgs.Empty);
    }
}
