using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using XmaX.ViewModels;

namespace XmaX.Pages;


/// <summary>
/// Settings page: language, theme, persist, auto-start, column count, widget layout, and revert.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;
    private bool _isInitializing = true;  // Prevent slider events during initialization

    public SettingsPage()
    {
        this.InitializeComponent();

        // Apply localized text from Loc
        SettingsTitle.Text = Loc.Nav_Settings;
        LanguageLabel.Text = Loc.Settings_Language;
        ThemeLabel.Text = Loc.Settings_Theme;
        PersistLabel.Text = Loc.Settings_Persist;
        PersistDescLabel.Text = Loc.Settings_PersistDesc;
        AutoStartLabel.Text = Loc.Settings_AutoStart;
        AutoStartDescLabel.Text = Loc.Settings_AutoStartDesc;
        ColumnsLabel.Text = Loc.Settings_Columns;
        RevertButton.Content = Loc.Button_RevertDefaults;
        FactoryResetLabel.Text = Loc.Settings_RestoreDefaults;
        FactoryResetDescLabel.Text = Loc.Settings_RestoreDefaultsDesc;
        FactoryResetButton.Content = Loc.Settings_RestoreDefaults;

        // Sub-page navigation labels are inside ControlTemplate — set after load
        this.Loaded += OnPageLoaded;

        _viewModel = new SettingsViewModel(App.Pipe, App.WidgetService);
        _viewModel.PropertyChanged += OnViewModelChanged;

        InitializeLanguageDropdown();
        InitializeThemeDropdown();
        InitializeColumnsSlider();
    }

    // ===== Initialization =====

    private void InitializeLanguageDropdown()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.Settings_LangAuto, Tag = "auto" });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.Settings_LangEn, Tag = "en" });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.Settings_LangZh, Tag = "zh" });

        // Select current
        var current = _viewModel.Language;
        for (int i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is ComboBoxItem item && (string)item.Tag == current)
            {
                LanguageCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void InitializeThemeDropdown()
    {
        ThemeCombo.Items.Clear();
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.Settings_ThemeSystem, Tag = "system" });
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.Settings_ThemeLight, Tag = "light" });
        ThemeCombo.Items.Add(new ComboBoxItem { Content = Loc.Settings_ThemeDark, Tag = "dark" });

        // Select current
        var current = _viewModel.Theme;
        for (int i = 0; i < ThemeCombo.Items.Count; i++)
        {
            if (ThemeCombo.Items[i] is ComboBoxItem item && (string)item.Tag == current)
            {
                ThemeCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void InitializeColumnsSlider()
    {
        _isInitializing = true;
        ColumnsSlider.Minimum = 3;
        ColumnsSlider.Maximum = 4;
        ColumnsSlider.Value = _viewModel.Columns;
        ColumnsValue.Text = _viewModel.Columns.ToString();
        _isInitializing = false;
    }

    // ===== Event handlers =====

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            _viewModel.Language = lang;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            _viewModel.Theme = theme;
        }
    }

    private void OnPersistToggled(object sender, RoutedEventArgs e)
    {
        _viewModel.Persist = PersistToggle.IsOn;
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        _viewModel.AutoStart = AutoStartToggle.IsOn;
    }

    private void OnColumnsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;

        var value = (int)ColumnsSlider.Value;
        ColumnsValue.Text = value.ToString();
        _viewModel.Columns = value;
    }

    private async void OnRevertClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Dialog_RevertTitle,
            Content = Loc.Dialog_RevertMessage,
            PrimaryButtonText = Loc.Button_Revert,
            CloseButtonText = Loc.Button_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _viewModel.RevertToDefaultsAsync();
        }
    }

    private async void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Settings_RestoreDefaults,
            Content = Loc.Settings_RestoreDefaultsConfirm,
            PrimaryButtonText = Loc.Button_Ok,
            CloseButtonText = Loc.Button_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                await App.Pipe.SendCommandAsync("restore_defaults").ConfigureAwait(true);
                // Reload config after reset
                await _viewModel.LoadConfigAsync().ConfigureAwait(true);
                // Update UI
                DispatcherQueue.TryEnqueue(() =>
                {
                    InitializeLanguageDropdown();
                    InitializeThemeDropdown();
                    PersistToggle.IsOn = _viewModel.Persist;
                    AutoStartToggle.IsOn = _viewModel.AutoStart;
                    InitializeColumnsSlider();
                });
            }
            catch
            {
                // Factory reset failed — show error or ignore
            }
        }
    }

    // ===== Initialization =====

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Set labels inside ControlTemplate (not accessible by x:Name in code-behind)
        SetTextInTemplate("ProfilesNavLabel", Loc.Title_Profiles);
        SetTextInTemplate("ProfilesNavDesc", Loc.Nav_ProfilesDesc);
        SetTextInTemplate("CoolingNavLabel", Loc.Title_FanCurves);
        SetTextInTemplate("CoolingNavDesc", Loc.Nav_CoolingDesc);
        SetTextInTemplate("PowerStatesNavLabel", Loc.Title_PowerStateAssignments);
        SetTextInTemplate("PowerStatesNavDesc", Loc.Nav_PowerStatesDesc);
        SetTextInTemplate("PlaygroundNavLabel", Loc.Title_WidgetPlayground);
        SetTextInTemplate("PlaygroundNavDesc", Loc.Nav_PlaygroundDesc);
    }

    private void SetTextInTemplate(string name, string text)
    {
        if (FindVisualChild<TextBlock>(this, name) is TextBlock tb)
        {
            tb.Text = text;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name)
                return fe;
            var result = FindVisualChild<T>(child, name);
            if (result != null)
                return result;
        }
        return null;
    }

    // ===== Navigation =====

    private void OnBackClick(object sender, RoutedEventArgs e) => App.NavigateBack();

    // ===== Sub-page navigation =====

    private static readonly SlideNavigationTransitionInfo SlideFromRight =
        new() { Effect = SlideNavigationTransitionEffect.FromRight };

    private void OnNavigateProfiles(object sender, RoutedEventArgs e)
    {
        App.NavigateTo(typeof(ProfilesSubPage), SlideFromRight);
    }

    private void OnNavigateCooling(object sender, RoutedEventArgs e)
    {
        App.NavigateTo(typeof(CoolingSubPage), SlideFromRight);
    }

    private void OnNavigatePowerStates(object sender, RoutedEventArgs e)
    {
        App.NavigateTo(typeof(PowerStatesSubPage), SlideFromRight);
    }

    private void OnNavigatePlayground(object sender, RoutedEventArgs e)
    {
        App.NavigateTo(typeof(WidgetPlaygroundSubPage), SlideFromRight);
    }

    // ===== ViewModel sync =====

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Config))
        {
            // Config loaded — update UI
            DispatcherQueue.TryEnqueue(() =>
            {
                InitializeLanguageDropdown();
                InitializeThemeDropdown();
                PersistToggle.IsOn = _viewModel.Persist;
                AutoStartToggle.IsOn = _viewModel.AutoStart;
                InitializeColumnsSlider();
            });
        }
    }
}
