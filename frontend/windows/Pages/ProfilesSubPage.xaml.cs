using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.Models;
using XmaX.ViewModels;

namespace XmaX.Pages;

/// <summary>
/// Profiles sub-page: profile CRUD with create/edit/delete.
/// Navigated from Settings page drill-down.
/// </summary>
public sealed partial class ProfilesSubPage : Page
{
    private readonly ProfilesViewModel _viewModel;

    public ProfilesSubPage()
    {
        this.InitializeComponent();

        PageTitle.Text = Loc.Title_Profiles;
        CreateProfileBtn.Content = Loc.Button_CreateProfile;

        _viewModel = App.GetProfilesViewModel();
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ProfilesViewModel.Profiles))
                DispatcherQueue.TryEnqueue(RebuildProfilesList);
        };

        RebuildProfilesList();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => App.NavigateBack();

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
                panel.Children.Add(BuildProfileCard(profile));
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Type icon (adaptive: effe, fixed: edde)
        var icon = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("/Assets/tabler-icons.ttf#tabler-icons"),
            Text = profile.IsAdaptive ? "\U0000EFFE" : "\U0000EDDE",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = profile.Name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        info.Children.Add(new TextBlock
        {
            Text = Loc.F("format.tdp_fan", profile.Tdp.Stapm, profile.Tdp.Fast, profile.Tdp.Slow, profile.FanCurve),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var editBtn = new Button
        {
            Content = Loc.Button_Edit,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = profile,
        };
        editBtn.Click += OnEditProfileClick;
        Grid.SetColumn(editBtn, 2);
        grid.Children.Add(editBtn);

        var deleteBtn = new Button
        {
            Content = Loc.Button_Delete,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = profile,
        };
        deleteBtn.Click += OnDeleteProfileClick;
        Grid.SetColumn(deleteBtn, 3);
        grid.Children.Add(deleteBtn);

        return grid;
    }

    // ===== Profile CRUD =====

    private async void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileEditorDialog(null, _viewModel.FanCurves.ToList(), _viewModel.Profiles.ToList()) { XamlRoot = this.XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && dialog.ResultProfile != null)
        {
            try
            {
                var p = dialog.ResultProfile;
                if (p.IsAdaptive)
                {
                    await _viewModel.CreateAdaptiveProfileAsync(p.Name, p.Tuning, p.TargetTempC, p.TdpMaxW, p.FanMaxPercent, p.PowerState);
                }
                else
                {
                    await _viewModel.CreateFixedProfileAsync(p.Name, p.Tdp.Stapm, p.Tdp.Fast, p.Tdp.Slow, p.FanCurve, p.PowerState);
                }
            }
            catch (Exception ex) { await ShowErrorAsync(Loc.Dialog_CreateFailed, ex.Message); }
        }
    }

    private async void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Profile profile)
        {
            var dialog = new ProfileEditorDialog(profile, _viewModel.FanCurves.ToList(), _viewModel.Profiles.ToList()) { XamlRoot = this.XamlRoot };
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary && dialog.ResultProfile != null)
            {
                try
                {
                    await _viewModel.UpdateProfileAsync(dialog.ResultProfile);
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
