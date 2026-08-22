using CommunityToolkit.WinUI.Controls;
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

        CreateProfileBtn.Content = Loc.Button_CreateProfile;

        _viewModel = App.GetProfilesViewModel();
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ProfilesViewModel.Profiles))
                DispatcherQueue.TryEnqueue(RebuildProfilesList);
        };

        RebuildProfilesList();
    }

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

    private SettingsExpander BuildProfileCard(Profile profile)
    {
        var expander = new SettingsExpander
        {
            Header = profile.Name,
            Description = profile.IsAdaptive ? GetTuningDisplayText(profile.Tuning) : GetTdpDisplayText(profile.Tdp),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        // Header icon (adaptive: effe, fixed: edde)
        expander.HeaderIcon = new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("/Assets/tabler-icons-300.ttf#tabler-icons"),
            Glyph = profile.IsAdaptive ? "\U0000EFFE" : "\U0000EDDE",
            FontSize = 20,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        };

        // Expanded items: Details + Actions
        // Type row
        var typeCard = new SettingsCard
        {
            Header = "Type",
            Content = new TextBlock
            {
                Text = profile.IsAdaptive ? $"Adaptive: {GetTuningDisplayText(profile.Tuning)}" : "Fixed",
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        expander.Items.Add(typeCard);

        // Power State row
        var powerStateCard = new SettingsCard
        {
            Header = "Power State",
            Content = new TextBlock
            {
                Text = profile.PowerState ?? "None",
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        expander.Items.Add(powerStateCard);

        // TDP row (only for fixed profiles)
        if (!profile.IsAdaptive)
        {
            var tdpCard = new SettingsCard
            {
                Header = Loc.Form_Tdp,
                Content = new TextBlock
                {
                    Text = $"{profile.Tdp.Stapm}W • {profile.Tdp.Fast}W • {profile.Tdp.Slow}W",
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            expander.Items.Add(tdpCard);
        }

        // Fan Curve row (only for fixed profiles)
        if (!profile.IsAdaptive)
        {
            var fanCurveCard = new SettingsCard
            {
                Header = Loc.Form_FanCurve,
                Content = new TextBlock
                {
                    Text = profile.FanCurve,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            expander.Items.Add(fanCurveCard);
        }

        // Edit button
        var editCard = new SettingsCard
        {
            Header = "Edit this profile",
        };
        var editBtn = new Button
        {
            Content = Loc.Button_Edit,
            Tag = profile,
        };
        editBtn.Click += OnEditProfileClick;
        editCard.Content = editBtn;
        expander.Items.Add(editCard);

        // Remove button
        var removeCard = new SettingsCard
        {
            Header = "Remove this profile",
        };
        var removeBtn = new Button
        {
            Content = "Remove",
            Tag = profile,
        };
        removeBtn.Click += OnDeleteProfileClick;
        removeCard.Content = removeBtn;
        expander.Items.Add(removeCard);

        return expander;
    }

    // ===== Profile CRUD =====

    private void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        // Navigate to editor page for new profile
        var settingsPage = GetParentSettingsPage();
        settingsPage?.NavigateToSubPage(typeof(ProfileEditorPage), parameter: null);
    }

    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Profile profile)
        {
            // Navigate to editor page with the existing profile
            var settingsPage = GetParentSettingsPage();
            settingsPage?.NavigateToSubPage(typeof(ProfileEditorPage), parameter: profile);
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

    // ===== Helper methods =====

    private static string GetTuningDisplayText(string tuning)
    {
        return tuning switch
        {
            "silent" => Loc.Button_Silent,
            "default" => Loc.Button_Default,
            "performance" => Loc.Button_Performance,
            _ => tuning
        };
    }

    private static string GetTdpDisplayText(TdpLimits tdp)
    {
        return $"STAPM: {tdp.Stapm}W • Fast: {tdp.Fast}W • Slow: {tdp.Slow}W";
    }
}
