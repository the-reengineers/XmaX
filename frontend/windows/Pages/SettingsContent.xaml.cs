using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using XmaX.Models;
using XmaX.Services;
using XmaX.ViewModels;

namespace XmaX.Pages;

/// <summary>
/// Settings content page: language, theme, persist, auto-start, column count, widget layout, and revert.
/// Hosted inside SettingsPage which manages the breadcrumb header and sub-page navigation.
/// </summary>
public sealed partial class SettingsContent : Page
{
    private readonly SettingsViewModel _viewModel;
    private bool _isInitializing = true;  // Prevent slider events during initialization

    public SettingsContent()
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

        // UMA labels
        UmaLabel.Text = Loc.Settings_Uma;
        UmaDescLabel.Text = Loc.Settings_UmaDesc;

        // Sub-page navigation labels are inside ControlTemplate — set after load
        this.Loaded += OnPageLoaded;

        _viewModel = new SettingsViewModel(App.Pipe, App.WidgetService);
        _viewModel.PropertyChanged += OnViewModelChanged;

        // Subscribe to UmaService updates (data arrives async after backend connects)
        App.UmaService.PropertyChanged += OnUmaServiceChanged;

        InitializeLanguageDropdown();
        InitializeThemeDropdown();
        InitializeColumnsSlider();
        InitializeUma();
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

    private void InitializeUma()
    {
        var umaService = App.UmaService;
        Logger.Debug($"[UMA] InitializeUma: Supported={umaService.Supported}, Options={umaService.AvailableOptions.Count}, CurrentOption={umaService.CurrentOption?.Name ?? "null"}");

        if (!umaService.Supported || umaService.AvailableOptions.Count == 0)
        {
            Logger.Debug("[UMA] InitializeUma: hiding UmaPanel");
            UmaPanel.Visibility = Visibility.Collapsed;
            return;
        }

        Logger.Debug("[UMA] InitializeUma: showing UmaPanel, populating combo box");
        UmaPanel.Visibility = Visibility.Visible;

        // Defer item population until ComboBox is in the visual tree
        // so we can inherit its font properties for accurate text measurement.
        _isInitializing = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            PopulateUmaItems();
            _isInitializing = false;
        });
    }

    private void PopulateUmaItems()
    {
        var umaService = App.UmaService;
        UmaCombo.Items.Clear();

        // Read font properties from the ComboBox (inherited by items)
        var fontFamily = UmaCombo.FontFamily;
        double fontSize = UmaCombo.FontSize > 0 ? UmaCombo.FontSize : 14;

        // Measure max value width for fixed-width number columns (numbers right-align within).
        // Add 8px buffer for ComboBoxItem internal padding / sub-pixel rendering differences.
        double maxValWidth = umaService.AvailableOptions.Max(o => Math.Max(
            MeasureTextWidth(o.MemoryCarvedGb.ToString("F1"), fontFamily, fontSize),
            MeasureTextWidth(o.MemoryRemainingGb.ToString("F1"), fontFamily, fontSize))) + 8;

        // Group options by mode (auto first, then custom)
        var autoOptions = umaService.AvailableOptions.Where(o => o.Mode == "auto").ToList();
        var customOptions = umaService.AvailableOptions.Where(o => o.Mode != "auto").ToList();

        // Add auto options
        foreach (var option in autoOptions)
        {
            UmaCombo.Items.Add(CreateUmaItem(option, maxValWidth));
        }

        // Add separator between auto and custom groups
        if (autoOptions.Count > 0 && customOptions.Count > 0)
        {
            var separator = new Border
            {
                Height = 1,
                Margin = new Thickness(8, 4, 8, 4),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray),
                Opacity = 0.3,
            };
            UmaCombo.Items.Add(separator);
        }

        // Add custom options
        foreach (var option in customOptions)
        {
            UmaCombo.Items.Add(CreateUmaItem(option, maxValWidth));
        }

        // Find current option by tag id (works regardless of separator position)
        if (umaService.CurrentOption != null)
        {
            for (int i = 0; i < UmaCombo.Items.Count; i++)
            {
                if (UmaCombo.Items[i] is ComboBoxItem cbi &&
                    cbi.Tag is string id && id == umaService.CurrentOption.Id)
                {
                    UmaCombo.SelectedIndex = i;
                    _umaPreviousIndex = i;
                    break;
                }
            }
        }
    }

    private static ComboBoxItem CreateUmaItem(UmaOption option, double maxValWidth)
    {
        // Col 0: Name (Star — fills available width, left-aligned)
        var nameBlock = new TextBlock
        {
            Text = option.Name,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Col 1: "Graphics" label
        var graphicsLabel = new TextBlock
        {
            Text = "Graphics",
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Col 2: VRAM value (right-aligned in fixed-width column → numbers align)
        var vramBlock = new TextBlock
        {
            Text = option.MemoryCarvedGb.ToString("F1"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        };
        // Col 3: "GB" unit
        var graphicsUnit = new TextBlock
        {
            Text = "GB",
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Col 4: "System" label
        var systemLabel = new TextBlock
        {
            Text = "System",
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Col 5: RAM value (right-aligned in fixed-width column → numbers align)
        var ramBlock = new TextBlock
        {
            Text = option.MemoryRemainingGb.ToString("F1"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        };
        // Col 6: "GB" unit (Star — fills remaining width so Grid stretches)
        var systemUnit = new TextBlock
        {
            Text = "GB",
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(maxValWidth, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(maxValWidth, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(nameBlock, 0);
        Grid.SetColumn(graphicsLabel, 1);
        Grid.SetColumn(vramBlock, 2);
        Grid.SetColumn(graphicsUnit, 3);
        Grid.SetColumn(systemLabel, 4);
        Grid.SetColumn(ramBlock, 5);
        Grid.SetColumn(systemUnit, 6);

        grid.Children.Add(nameBlock);
        grid.Children.Add(graphicsLabel);
        grid.Children.Add(vramBlock);
        grid.Children.Add(graphicsUnit);
        grid.Children.Add(systemLabel);
        grid.Children.Add(ramBlock);
        grid.Children.Add(systemUnit);

        return new ComboBoxItem
        {
            Content = grid,
            Tag = option.Id,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private double MeasureTextWidth(string text, Microsoft.UI.Xaml.Media.FontFamily fontFamily, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize,
        };
        tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    private bool _umaChanging = false;
    private int _umaPreviousIndex = 0;

    private async void OnUmaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _umaChanging) return;

        int newIndex = UmaCombo.SelectedIndex;
        if (newIndex < 0 || newIndex >= UmaCombo.Items.Count) return;

        // Skip separator (Border, no Tag) — revert to previous valid selection
        if (UmaCombo.Items[newIndex] is not ComboBoxItem selectedItem ||
            selectedItem.Tag is not string selectedId)
        {
            _umaChanging = true;
            UmaCombo.SelectedIndex = _umaPreviousIndex;
            _umaChanging = false;
            return;
        }

        var umaService = App.UmaService;
        var selectedOption = umaService.AvailableOptions.FirstOrDefault(o => o.Id == selectedId);
        if (selectedOption == null) return;

        // Check if this is already the current option
        if (umaService.CurrentOption?.Id == selectedOption.Id) return;

        _umaChanging = true;

        // Dialog 1: Confirm that applying requires a reboot
        var applyDialog = new ContentDialog
        {
            Title = Loc.Settings_Uma,
            Content = Loc.Settings_UmaRebootWarning,
            PrimaryButtonText = Loc.Button_Save,
            CloseButtonText = Loc.Button_Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        // Prevent dialog from closing via light dismiss (clicking outside)
        // Track whether a button was clicked to allow proper closing
        bool buttonClicked = false;
        applyDialog.PrimaryButtonClick += (s, args) => buttonClicked = true;
        applyDialog.CloseButtonClick += (s, args) => buttonClicked = true;
        applyDialog.Closing += (s, args) =>
        {
            // Only allow closing if a button was clicked
            if (!buttonClicked)
            {
                args.Cancel = true;
            }
        };

        // Suppress main window deactivation while dialog is open
        App.MainWindow?.SetModalDialogOpen(true);
        try
        {
            var applyResult = await applyDialog.ShowAsync();
            if (applyResult != ContentDialogResult.Primary)
            {
                // Cancelled — revert to previous selection
                _umaChanging = true;
                UmaCombo.SelectedIndex = _umaPreviousIndex;
                _umaChanging = false;
                return;
            }

            // Send the UMA option to the backend
            // Backend will reboot automatically after setting the option
            var success = await umaService.SetOptionAsync(selectedOption.Id);
            if (!success)
            {
                // Show error and revert selection
                var errorDialog = new ContentDialog
                {
                    Title = Loc.Dialog_Error,
                    Content = Loc.Settings_UmaApplyFailed,
                    CloseButtonText = Loc.Button_Ok,
                    XamlRoot = this.XamlRoot,
                };
                await errorDialog.ShowAsync();

                _umaChanging = true;
                UmaCombo.SelectedIndex = _umaPreviousIndex;
                _umaChanging = false;
                return;
            }

            // Success — backend will reboot automatically (2s delay)
            _umaPreviousIndex = newIndex;
            _umaChanging = false;
        }
        finally
        {
            App.MainWindow?.SetModalDialogOpen(false);
        }
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

    // ===== Sub-page navigation =====

    private static readonly SlideNavigationTransitionInfo SlideFromRight =
        new() { Effect = SlideNavigationTransitionEffect.FromRight };

    private void OnNavigateProfiles(object sender, RoutedEventArgs e)
    {
        // Navigate within the parent SettingsPage's frame
        var settingsPage = GetParentSettingsPage();
        settingsPage?.NavigateToSubPage(typeof(ProfilesSubPage), SlideFromRight);
    }

    private void OnNavigateCooling(object sender, RoutedEventArgs e)
    {
        var settingsPage = GetParentSettingsPage();
        settingsPage?.NavigateToSubPage(typeof(CoolingSubPage), SlideFromRight);
    }

    private void OnNavigatePowerStates(object sender, RoutedEventArgs e)
    {
        var settingsPage = GetParentSettingsPage();
        settingsPage?.NavigateToSubPage(typeof(PowerStatesSubPage), SlideFromRight);
    }

    private void OnNavigatePlayground(object sender, RoutedEventArgs e)
    {
        var settingsPage = GetParentSettingsPage();
        settingsPage?.NavigateToSubPage(typeof(WidgetPlaygroundSubPage), SlideFromRight);
    }

    private SettingsPage? GetParentSettingsPage()
    {
        // Walk up the visual tree to find the parent SettingsPage
        DependencyObject current = this;
        while (current != null)
        {
            if (current is SettingsPage page)
                return page;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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

    private void OnUmaServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Logger.Debug($"[UMA] OnUmaServiceChanged: property={e.PropertyName}, Supported={App.UmaService.Supported}, Options={App.UmaService.AvailableOptions.Count}");

        if (e.PropertyName == nameof(UmaService.Supported) || e.PropertyName == nameof(UmaService.AvailableOptions))
        {
            // UMA data arrived from backend — rebuild UI
            DispatcherQueue.TryEnqueue(() =>
            {
                Logger.Debug($"[UMA] OnUmaServiceChanged: dispatching InitializeUma on UI thread");
                InitializeUma();
            });
        }
        else if (e.PropertyName == nameof(UmaService.CurrentOption))
        {
            // CurrentOption updated (e.g. backend reports new current after set, or after reconnect)
            // — just update the ComboBox selection without rebuilding items
            DispatcherQueue.TryEnqueue(() =>
            {
                var currentId = App.UmaService.CurrentOption?.Id;
                if (currentId == null || _umaChanging) return;
                for (int i = 0; i < UmaCombo.Items.Count; i++)
                {
                    if (UmaCombo.Items[i] is ComboBoxItem cbi &&
                        cbi.Tag is string id && id == currentId)
                    {
                        if (UmaCombo.SelectedIndex != i)
                        {
                            _isInitializing = true;
                            UmaCombo.SelectedIndex = i;
                            _umaPreviousIndex = i;
                            _isInitializing = false;
                        }
                        break;
                    }
                }
            });
        }
    }
}
