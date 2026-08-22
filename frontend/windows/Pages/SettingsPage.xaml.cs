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
        this.KeyDown += OnKeyDown;

        // Navigate to the settings content initially (no animation)
        SubPageFrame.Navigate(typeof(SettingsContent), null, new SuppressNavigationTransitionInfo());
    }

    // ===== Navigation =====

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Backspace or Alt+Left: Go back
        if (e.Key == Windows.System.VirtualKey.Back ||
            (e.Key == Windows.System.VirtualKey.Left && e.KeyStatus.IsMenuKeyDown))
        {
            HandleGoBack();
            e.Handled = true;
        }
        // Alt+Right: Go forward
        else if (e.Key == Windows.System.VirtualKey.Right && e.KeyStatus.IsMenuKeyDown)
        {
            HandleGoForward();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Navigate back in the sub-page frame (called from MainWindow keyboard handler).
    /// </summary>
    public void HandleGoBack()
    {
        if (SubPageFrame.CanGoBack)
        {
            SubPageFrame.GoBack(new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
        }
    }

    /// <summary>
    /// Navigate forward in the sub-page frame (called from MainWindow keyboard handler).
    /// </summary>
    public void HandleGoForward()
    {
        if (SubPageFrame.CanGoForward)
        {
            SubPageFrame.GoForward();
        }
    }

    private void OnNavigateHome()
    {
        App.NavigateTo(typeof(HomePage), new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft
        });
        // Update UI for home page (show edit button, settings icon)
        App.MainWindow?.UpdateUIForHomePage();
    }

    private void OnNavigateSettings()
    {
        // Remove all items after Settings to go back to settings content
        while (_breadcrumbItems.Count > 2)
        {
            _breadcrumbItems.RemoveAt(_breadcrumbItems.Count - 1);
        }
        SubPageFrame.Navigate(typeof(SettingsContent), null, new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft
        });
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
        else if (args.Index >= 2)
        {
            // Sub-page breadcrumb clicked — navigate back to that level
            // For now, just go back one level (e.g., Fan Curves > Curve Name → Fan Curves)
            if (SubPageFrame.CanGoBack)
            {
                SubPageFrame.GoBack(new SlideNavigationTransitionInfo
                {
                    Effect = SlideNavigationTransitionEffect.FromRight
                });
            }
        }
    }

    public void NavigateToSubPage(Type pageType, object? parameter = null, NavigationTransitionInfo? transitionInfo = null)
    {
        // Default to slide-from-right transition for forward navigation
        var transition = transitionInfo ?? new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromRight
        };
        SubPageFrame.Navigate(pageType, parameter, transition);
    }

    private void OnSubPageNavigated(object sender, NavigationEventArgs e)
    {
        UpdateBreadcrumb(e.SourcePageType, e.Content);
    }

    private void UpdateBreadcrumb(Type pageType, object? pageContent)
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

        // FanCurveEditorPage: Home > Settings > Fan Curves > [Curve Name]
        if (pageType == typeof(FanCurveEditorPage) && pageContent is FanCurveEditorPage editorPage)
        {
            _breadcrumbItems.Add(new BreadcrumbItem { Text = Loc.Title_FanCurves });
            _breadcrumbItems.Add(new BreadcrumbItem { Text = editorPage.GetPageTitle() });
            return;
        }

        // ProfileEditorPage: Home > Settings > Profiles > [Profile Name]
        if (pageType == typeof(ProfileEditorPage) && pageContent is ProfileEditorPage profileEditorPage)
        {
            _breadcrumbItems.Add(new BreadcrumbItem { Text = Loc.Title_Profiles });
            _breadcrumbItems.Add(new BreadcrumbItem { Text = profileEditorPage.GetPageTitle() });
            return;
        }

        // Sub-pages: Home > Settings > [Page Name]
        string? pageName = pageType switch
        {
            var t when t == typeof(ProfilesSubPage) => Loc.Title_Profiles,
            var t when t == typeof(CoolingSubPage) => Loc.Title_FanCurves,
            var t when t == typeof(PowerStatesSubPage) => Loc.Title_PowerStateAssignments,
            _ => null
        };

        if (pageName != null)
        {
            _breadcrumbItems.Add(new BreadcrumbItem { Text = pageName });
        }
    }
}
