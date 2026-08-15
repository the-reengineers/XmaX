using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;
using XmaX.Services;
using XmaX.ViewModels;

namespace XmaX.Pages;

/// <summary>
/// Profiles page: profile CRUD, fan curve CRUD, and power state assignments.
/// </summary>
public sealed partial class ProfilesPage : Page
{
    private readonly ProfilesViewModel _viewModel;
    private bool _suppressSliderChange;

    public ProfilesPage()
    {
        this.InitializeComponent();

        // Localize XAML-defined elements
        ProfilesTitle.Text = Loc.Title_Profiles;
        CreateProfileBtn.Content = Loc.Button_CreateProfile;
        PowerStateTitle.Text = Loc.Title_PowerStateAssignments;

        _viewModel = new ProfilesViewModel(App.ProfileService, App.Pipe);
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ProfilesViewModel.Profiles))
                DispatcherQueue.TryEnqueue(() => RebuildProfilesList());
            if (e.PropertyName == nameof(ProfilesViewModel.Config))
                DispatcherQueue.TryEnqueue(() => RebuildPowerStateUI());
        };

        RebuildProfilesList();
        BuildPowerStateUI();
    }

    // ===== Profiles List =====

    private void RebuildProfilesList()
    {
        ProfilesList.ItemsSource = null;
        var panel = new StackPanel { Spacing = 6 };

        if (_viewModel.Profiles.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Empty_NoProfilesHint,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        else
        {
            foreach (var profile in _viewModel.Profiles)
            {
                var card = BuildProfileCard(profile);
                panel.Children.Add(card);
            }
        }

        ProfilesList.ItemsSource = new List<UIElement> { panel };
        ProfilesList.ItemTemplate = null;
    }

    private Grid BuildProfileCard(Profile profile)
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
        info.Children.Add(new TextBlock { Text = profile.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        info.Children.Add(new TextBlock
        {
            Text = Loc.F("format.tdp_fan", profile.Tdp.Stapm, profile.Tdp.Fast, profile.Tdp.Slow, profile.FanCurve),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var editBtn = new Button { Content = Loc.Button_Edit, VerticalAlignment = VerticalAlignment.Center, Tag = profile };
        editBtn.Click += OnEditProfileClick;
        Grid.SetColumn(editBtn, 1);
        grid.Children.Add(editBtn);

        var deleteBtn = new Button { Content = Loc.Button_Delete, VerticalAlignment = VerticalAlignment.Center, Tag = profile };
        deleteBtn.Click += OnDeleteProfileClick;
        Grid.SetColumn(deleteBtn, 2);
        grid.Children.Add(deleteBtn);

        return grid;
    }

    // ===== Profile CRUD Handlers =====

    private async void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileEditorDialog(null, _viewModel.FanCurves.ToList()) { XamlRoot = this.XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && dialog.ResultProfile != null)
        {
            try
            {
                var p = dialog.ResultProfile;
                await _viewModel.CreateProfileAsync(p.Name, p.Tdp.Stapm, p.Tdp.Fast, p.Tdp.Slow, p.FanCurve);
            }
            catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_CreateFailed, ex.Message); }
        }
    }

    private async void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Profile profile)
        {
            var dialog = new ProfileEditorDialog(profile, _viewModel.FanCurves.ToList()) { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.ResultProfile != null)
            {
                try
                {
                    var updated = dialog.ResultProfile;
                    profile.Name = updated.Name;
                    profile.Tdp = updated.Tdp;
                    profile.FanCurve = updated.FanCurve;
                    await _viewModel.UpdateProfileAsync(profile);
                }
                catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_UpdateFailed, ex.Message); }
            }
        }
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Profile profile)
        {
            try
            {
                await _viewModel.DeleteProfileAsync(profile.Id);
            }
            catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_DeleteFailed, ex.Message); }
        }
    }

    // ===== Power State Assignments =====

    private void BuildPowerStateUI()
    {
        PowerStateAssignments.Children.Clear();

        var states = new (string Id, string Label)[]
        {
            ("battery", Loc.Power_Battery),
            ("usb_c_slow", Loc.Power_UsbCSlow),
            ("usb_c_fast", Loc.Power_UsbCFast),
            ("dc_in", Loc.Power_DcIn),
        };

        foreach (var (id, label) in states)
        {
            var row = new Grid
            {
                Padding = new Thickness(8),
                ColumnSpacing = 12,
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            // Label
            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            Grid.SetColumn(labelBlock, 0);
            row.Children.Add(labelBlock);

            // Profile dropdown
            var profileCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = id,
            };
            profileCombo.Items.Add(Loc.Form_None);
            foreach (var p in _viewModel.Profiles)
            {
                profileCombo.Items.Add(p);
            }
            profileCombo.DisplayMemberPath = nameof(Profile.Name);
            profileCombo.SelectionChanged += (s, e) => OnPowerStateProfileChanged(id, profileCombo);
            Grid.SetColumn(profileCombo, 1);
            row.Children.Add(profileCombo);

            // TDP max slider
            var tdpSlider = new Slider
            {
                Minimum = 6,
                Maximum = 120,
                StepFrequency = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = id,
            };
            tdpSlider.ValueChanged += (s, e) => OnPowerStateTdpChanged(id, tdpSlider);
            Grid.SetColumn(tdpSlider, 2);
            row.Children.Add(tdpSlider);

            PowerStateAssignments.Children.Add(row);

            // Set initial values from config
            UpdatePowerStateUI(id, profileCombo, tdpSlider);
        }
    }

    private void RebuildPowerStateUI()
    {
        // Rebuild to update profile dropdowns with new profiles
        BuildPowerStateUI();
    }

    private void UpdatePowerStateUI(string stateId, ComboBox profileCombo, Slider tdpSlider)
    {
        var psp = _viewModel.Config.PowerStateProfiles;
        PowerStateAssignment? assignment = stateId switch
        {
            "battery" => psp.Battery,
            "usb_c_slow" => psp.UsbCSlow,
            "usb_c_fast" => psp.UsbCFast,
            "dc_in" => psp.DcIn,
            _ => null,
        };

        if (assignment != null)
        {
            var profile = _viewModel.Profiles.FirstOrDefault(p => p.Id == assignment.Profile);
            _suppressSliderChange = true;
            profileCombo.SelectedItem = profile ?? profileCombo.Items[0];
            tdpSlider.Value = assignment.TdpMaxW;
            _suppressSliderChange = false;
        }
    }

    private async void OnPowerStateProfileChanged(string stateId, ComboBox combo)
    {
        if (_suppressSliderChange) return;

        string profileSlug = combo.SelectedItem is Profile p ? p.Id : "";
        var psp = _viewModel.Config.PowerStateProfiles;
        var currentTdp = stateId switch
        {
            "battery" => psp.Battery.TdpMaxW,
            "usb_c_slow" => psp.UsbCSlow.TdpMaxW,
            "usb_c_fast" => psp.UsbCFast.TdpMaxW,
            "dc_in" => psp.DcIn.TdpMaxW,
            _ => 25,
        };

        try
        {
            await _viewModel.SetPowerStateAssignmentAsync(stateId, profileSlug, currentTdp);
        }
        catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_UpdateFailed, ex.Message); }
    }

    private async void OnPowerStateTdpChanged(string stateId, Slider slider)
    {
        if (_suppressSliderChange) return;

        var psp = _viewModel.Config.PowerStateProfiles;
        var currentProfile = stateId switch
        {
            "battery" => psp.Battery.Profile,
            "usb_c_slow" => psp.UsbCSlow.Profile,
            "usb_c_fast" => psp.UsbCFast.Profile,
            "dc_in" => psp.DcIn.Profile,
            _ => "",
        };

        try
        {
            await _viewModel.SetPowerStateAssignmentAsync(stateId, currentProfile, (int)slider.Value);
        }
        catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_UpdateFailed, ex.Message); }
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
