using System.Collections.ObjectModel;
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

    // Widget layout items for display
    private ObservableCollection<WidgetLayoutItem> _widgetItems = new();

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
        WidgetLayoutLabel.Text = Loc.Settings_WidgetLayout;
        RevertButton.Content = Loc.Button_RevertDefaults;
        FactoryResetLabel.Text = Loc.Settings_RestoreDefaults;
        FactoryResetDescLabel.Text = Loc.Settings_RestoreDefaultsDesc;
        FactoryResetButton.Content = Loc.Settings_RestoreDefaults;

        _viewModel = new SettingsViewModel(App.Pipe, App.WidgetService);
        _viewModel.PropertyChanged += OnViewModelChanged;

        InitializeLanguageDropdown();
        InitializeThemeDropdown();
        InitializeColumnsSlider();

        // Build widget layout list after a short delay to let widgets register
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(100); // Let widgets register
            BuildWidgetLayoutList();
        });
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

    private void OnWidgetVisibilityToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.Tag is string widgetId)
        {
            _viewModel.ToggleWidgetVisibility(widgetId);
        }
    }

    private void OnMoveWidgetUp(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string widgetId)
        {
            _viewModel.MoveWidgetUp(widgetId);
            BuildWidgetLayoutList();
        }
    }

    private void OnMoveWidgetDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string widgetId)
        {
            _viewModel.MoveWidgetDown(widgetId);
            BuildWidgetLayoutList();
        }
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
                    BuildWidgetLayoutList();
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
        else if (e.PropertyName == nameof(SettingsViewModel.WidgetOrder))
        {
            DispatcherQueue.TryEnqueue(BuildWidgetLayoutList);
        }
    }

    // ===== Widget layout =====

    private void BuildWidgetLayoutList()
    {
        _widgetItems.Clear();

        foreach (var widgetId in _viewModel.WidgetOrder)
        {
            var label = GetWidgetLabel(widgetId);
            var isVisible = _viewModel.IsWidgetVisible(widgetId);

            _widgetItems.Add(new WidgetLayoutItem
            {
                WidgetId = widgetId,
                Label = label,
                IsVisible = isVisible,
            });
        }

        WidgetLayoutList.ItemsSource = _widgetItems;
    }

    private static string GetWidgetLabel(string widgetId)
    {
        // Map widget IDs to display names
        return widgetId switch
        {
            "profiles" => Loc.Widget_Profiles,
            "metrics" => Loc.Widget_Metrics,
            "adaptive" => Loc.Widget_Adaptive,
            "charge_limit" => Loc.Widget_ChargeLimit,
            "power" => Loc.Widget_Power,
            _ => widgetId,
        };
    }
}

/// <summary>
/// Display item for widget layout editor.
/// </summary>
public sealed class WidgetLayoutItem : System.ComponentModel.INotifyPropertyChanged
{
    public string WidgetId { get; set; } = "";
    public string Label { get; set; } = "";

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
