using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Models;

namespace XmaX.Pages;

/// <summary>
/// Dialog for creating or editing a profile.
/// </summary>
public sealed class ProfileEditorDialog : ContentDialog
{
    private readonly bool _isEdit;

    private TextBox _nameBox = null!;
    private Slider _stapmSlider = null!;
    private Slider _fastSlider = null!;
    private Slider _slowSlider = null!;
    private ComboBox _fanCurveCombo = null!;

    /// <summary>The resulting profile after OK, or null if cancelled.</summary>
    public Profile? ResultProfile { get; private set; }

    public ProfileEditorDialog(Profile? existingProfile, List<FanCurve> fanCurves)
    {
        _isEdit = existingProfile != null;

        Title = _isEdit ? Loc.Dialog_EditProfile : Loc.Dialog_CreateProfile;
        PrimaryButtonText = _isEdit ? Loc.Button_Save : Loc.Button_Create;
        CloseButtonText = Loc.Button_Cancel;
        DefaultButton = ContentDialogButton.Primary;

        InitializeContent();
        PrimaryButtonClick += OnPrimaryButtonClick;

        // Populate fields
        if (existingProfile != null)
        {
            _nameBox.Text = existingProfile.Name;
            _stapmSlider.Value = existingProfile.Tdp.Stapm;
            _fastSlider.Value = existingProfile.Tdp.Fast;
            _slowSlider.Value = existingProfile.Tdp.Slow;

            _fanCurveCombo.Items.Add(Loc.Form_None);
            foreach (var fc in fanCurves)
            {
                _fanCurveCombo.Items.Add(fc);
            }
            _fanCurveCombo.DisplayMemberPath = nameof(FanCurve.Name);

            var selected = fanCurves.FirstOrDefault(f => f.Id == existingProfile.FanCurve);
            _fanCurveCombo.SelectedItem = selected ?? _fanCurveCombo.Items[0];
        }
        else
        {
            _stapmSlider.Value = 45;
            _fastSlider.Value = 50;
            _slowSlider.Value = 45;

            _fanCurveCombo.Items.Add(Loc.Form_None);
            foreach (var fc in fanCurves)
            {
                _fanCurveCombo.Items.Add(fc);
            }
            _fanCurveCombo.DisplayMemberPath = nameof(FanCurve.Name);
            _fanCurveCombo.SelectedIndex = 0;
        }
    }

    private void InitializeContent()
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 300 };

        _nameBox = new TextBox { Header = Loc.Form_Name, PlaceholderText = Loc.Form_ProfileName };
        panel.Children.Add(_nameBox);

        _stapmSlider = new Slider { Header = Loc.Form_Stapm, Minimum = 6, Maximum = 120, StepFrequency = 1 };
        panel.Children.Add(_stapmSlider);

        _fastSlider = new Slider { Header = Loc.Form_Fast, Minimum = 6, Maximum = 120, StepFrequency = 1 };
        panel.Children.Add(_fastSlider);

        _slowSlider = new Slider { Header = Loc.Form_Slow, Minimum = 6, Maximum = 120, StepFrequency = 1 };
        panel.Children.Add(_slowSlider);

        _fanCurveCombo = new ComboBox { Header = Loc.Form_FanCurve, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(_fanCurveCombo);

        Content = panel;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            args.Cancel = true;
            return;
        }

        string? fanCurveId = null;
        if (_fanCurveCombo.SelectedItem is FanCurve fc)
        {
            fanCurveId = fc.Id;
        }

        ResultProfile = new Profile
        {
            Name = name,
            Tdp = new TdpLimits
            {
                Stapm = (int)_stapmSlider.Value,
                Fast = (int)_fastSlider.Value,
                Slow = (int)_slowSlider.Value,
            },
            FanCurve = fanCurveId,
        };
    }
}
