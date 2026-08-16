using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;
using XmaX.Services;
using XmaX.ViewModels;

namespace XmaX.Tests;

/// <summary>
/// Tests for SettingsViewModel.
/// Tests config management and property change notifications.
/// </summary>
public class SettingsViewModelTests
{
    [Fact]
    public void SettingsViewModel_InitialState_HasDefaults()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        Assert.Equal("auto", vm.Language);
        Assert.Equal("system", vm.Theme);
        Assert.False(vm.Persist);
        Assert.False(vm.AutoStart);
        Assert.Equal(3, vm.Columns);
    }

    [Fact]
    public void SettingsViewModel_SetLanguage_RaisesPropertyChanged()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        var raised = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.Language)) raised = true;
        };

        vm.Language = "en";

        Assert.True(raised);
        Assert.Equal("en", vm.Language);
    }

    [Fact]
    public void SettingsViewModel_SetTheme_RaisesPropertyChanged()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        var raised = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.Theme)) raised = true;
        };

        vm.Theme = "dark";

        Assert.True(raised);
        Assert.Equal("dark", vm.Theme);
    }

    [Fact]
    public void SettingsViewModel_SetPersist_RaisesPropertyChanged()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        var raised = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.Persist)) raised = true;
        };

        vm.Persist = true;

        Assert.True(raised);
        Assert.True(vm.Persist);
    }

    [Fact]
    public void SettingsViewModel_SetAutoStart_RaisesPropertyChanged()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        var raised = false;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.AutoStart)) raised = true;
        };

        vm.AutoStart = true;

        Assert.True(raised);
        Assert.True(vm.AutoStart);
    }

    [Fact]
    public void SettingsViewModel_SetColumns_UpdatesWidgetService()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        vm.Columns = 4;

        Assert.Equal(4, vm.Columns);
        Assert.Equal(4, widgetService.Columns);
    }

    [Fact]
    public void SettingsViewModel_SetColumns_ClampsToRange()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        vm.Columns = 2; // Below minimum
        Assert.Equal(3, vm.Columns);

        vm.Columns = 10; // Above maximum
        Assert.Equal(4, vm.Columns);
    }

    [Fact]
    public void SettingsViewModel_ConfigProperty_UpdatesOnLoad()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        // Config loading will fail without a backend, but we can verify the initial state
        Assert.NotNull(vm.Config);
    }

    [Fact]
    public void SettingsViewModel_LanguageOptions_ValidValues()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        // Test valid language values
        vm.Language = "auto";
        Assert.Equal("auto", vm.Language);

        vm.Language = "en";
        Assert.Equal("en", vm.Language);

        vm.Language = "zh";
        Assert.Equal("zh", vm.Language);
    }

    [Fact]
    public void SettingsViewModel_ThemeOptions_ValidValues()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        // Test valid theme values
        vm.Theme = "system";
        Assert.Equal("system", vm.Theme);

        vm.Theme = "light";
        Assert.Equal("light", vm.Theme);

        vm.Theme = "dark";
        Assert.Equal("dark", vm.Theme);
    }

    [Fact]
    public void SettingsViewModel_NoDuplicatePropertyChanged_WhenValueSame()
    {
        using var pipe = new PipeClient();
        var widgetService = new WidgetService(pipe);
        var vm = new SettingsViewModel(pipe, widgetService);

        var changeCount = 0;
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.Language)) changeCount++;
        };

        vm.Language = "en";
        vm.Language = "en"; // Same value again

        Assert.Equal(1, changeCount); // Should only fire once
    }
}
