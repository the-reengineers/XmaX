using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace XmaX.Pages;

/// <summary>
/// Breadcrumb item model: can display either an icon or text.
/// </summary>
public class BreadcrumbItem
{
    public string? Text { get; set; }
    public string? IconGlyph { get; set; }
    public Visibility IconVisibility => IconGlyph != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => Text != null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Settings page container: manages breadcrumb header and sub-page navigation.
/// The settings content is hosted in SettingsContent page within SubPageFrame.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly ObservableCollection<BreadcrumbItem> _breadcrumbItems = new();

    public SettingsPage()
    {
        this.InitializeComponent();

        // Initialize breadcrumb with Home (icon) and Settings (text)
        _breadcrumbItems.Add(new BreadcrumbItem { IconGlyph = "" });
        _breadcrumbItems.Add(new BreadcrumbItem { Text = Loc.Nav_Settings });

        BreadcrumbBar.ItemsSource = _breadcrumbItems;

        SubPageFrame.Navigated += OnSubPageNavigated;

        // Navigate to the settings content initially
        SubPageFrame.Navigate(typeof(SettingsContent));
    }

    // ===== Navigation =====

    private void OnNavigateHome() => App.NavigateTo(typeof(HomePage));

    private void OnNavigateSettings()
    {
        // Remove all items after Settings to go back to settings content
        while (_breadcrumbItems.Count > 2)
        {
            _breadcrumbItems.RemoveAt(_breadcrumbItems.Count - 1);
        }
        SubPageFrame.Navigate(typeof(SettingsContent));
    }

    private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        // Index 0 = Home, Index 1 = Settings, Index 2+ = Sub-pages
        if (args.Index == 0)
        {
            OnNavigateHome();
        }
        else if (args.Index == 1)
        {
            OnNavigateSettings();
        }
    }

    public void NavigateToSubPage(Type pageType, NavigationTransitionInfo? transitionInfo = null)
    {
        if (transitionInfo != null)
            SubPageFrame.Navigate(pageType, null, transitionInfo);
        else
            SubPageFrame.Navigate(pageType);
    }

    private void OnSubPageNavigated(object sender, NavigationEventArgs e)
    {
        UpdateBreadcrumb(e.SourcePageType);
    }

    private void UpdateBreadcrumb(Type pageType)
    {
        // Remove any existing sub-page items (keep Home and Settings)
        while (_breadcrumbItems.Count > 2)
        {
            _breadcrumbItems.RemoveAt(_breadcrumbItems.Count - 1);
        }

        // SettingsContent: Home > Settings (already in the collection)
        if (pageType == typeof(SettingsContent))
        {
            return;
        }

        // Sub-pages: Home > Settings > [Page Name]
        string? pageName = pageType switch
        {
            var t when t == typeof(ProfilesSubPage) => Loc.Title_Profiles,
            var t when t == typeof(CoolingSubPage) => Loc.Title_FanCurves,
            var t when t == typeof(PowerStatesSubPage) => Loc.Title_PowerStateAssignments,
            var t when t == typeof(WidgetPlaygroundSubPage) => Loc.Title_WidgetPlayground,
            _ => null
        };

        if (pageName != null)
        {
            _breadcrumbItems.Add(new BreadcrumbItem { Text = pageName });
        }
    }
}
