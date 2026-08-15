using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
