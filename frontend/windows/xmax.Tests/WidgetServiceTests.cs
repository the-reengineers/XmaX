using XmaX.Models;
using XmaX.Services;

namespace XmaX.Tests;

public class WidgetServiceTests
{
    private static WidgetService CreateService()
    {
        using var pipe = new PipeClient();
        return new WidgetService(pipe);
    }

    // ===== Columns =====

    [Fact]
    public void Columns_Default_IsMinColumns()
    {
        var svc = CreateService();
        Assert.Equal(WidgetService.MinColumns, svc.Columns);
    }

    [Fact]
    public void Columns_SetValue_Clamped()
    {
        var svc = CreateService();

        svc.Columns = 4;
        Assert.Equal(4, svc.Columns);

        svc.Columns = 2; // Below min
        Assert.Equal(WidgetService.MinColumns, svc.Columns);

        svc.Columns = 10; // Above max
        Assert.Equal(WidgetService.MaxColumns, svc.Columns);
    }

    [Fact]
    public void Columns_SetSameValue_NoPropertyChanged()
    {
        var svc = CreateService();
        var initial = svc.Columns;

        var changed = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WidgetService.Columns)) changed = true;
        };

        svc.Columns = initial;
        Assert.False(changed);
    }

    // ===== ColumnWidth =====

    [Fact]
    public void ColumnWidth_Default_IsDefaultColumnWidth()
    {
        var svc = CreateService();
        Assert.Equal(WidgetService.DefaultColumnWidth, svc.ColumnWidth);
    }

    [Fact]
    public void ColumnWidth_SetValue_UpdatesAndFiresPropertyChanged()
    {
        var svc = CreateService();
        var changed = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WidgetService.ColumnWidth)) changed = true;
        };

        svc.ColumnWidth = 200;

        Assert.Equal(200, svc.ColumnWidth);
        Assert.True(changed);
    }

    [Fact]
    public void ColumnWidth_ZeroOrNegative_Ignored()
    {
        var svc = CreateService();
        svc.ColumnWidth = 0;
        Assert.Equal(WidgetService.DefaultColumnWidth, svc.ColumnWidth);
        svc.ColumnWidth = -10;
        Assert.Equal(WidgetService.DefaultColumnWidth, svc.ColumnWidth);
    }

    // ===== WindowHeightRows =====

    [Fact]
    public void WindowHeightRows_Default_IsDefaultWindowHeightRows()
    {
        var svc = CreateService();
        Assert.Equal(WidgetService.DefaultWindowHeightRows, svc.WindowHeightRows);
    }

    [Fact]
    public void WindowHeightRows_SetValue_UpdatesAndFiresPropertyChanged()
    {
        var svc = CreateService();
        var changed = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WidgetService.WindowHeightRows)) changed = true;
        };

        svc.WindowHeightRows = 3;

        Assert.Equal(3, svc.WindowHeightRows);
        Assert.True(changed);
    }

    [Fact]
    public void WindowHeightRows_ClampedToRange()
    {
        var svc = CreateService();
        svc.WindowHeightRows = 0;
        Assert.Equal(WidgetService.MinWindowHeightRows, svc.WindowHeightRows);

        svc.WindowHeightRows = 10;
        Assert.Equal(WidgetService.MaxWindowHeightRows, svc.WindowHeightRows);
    }

    // ===== LoadWidgetSpans / ConfigWidgetIds / GetWidgetSpan =====

    [Fact]
    public void LoadWidgetSpans_PopulatesConfigWidgetIds()
    {
        var svc = CreateService();
        var entries = new[]
        {
            new WidgetEntry { Id = "cpu", ColSpan = 1, RowSpan = 1 },
            new WidgetEntry { Id = "gpu", ColSpan = 2, RowSpan = 1 },
        };

        svc.LoadWidgetSpans(entries);

        Assert.Equal(2, svc.ConfigWidgetIds.Count);
        Assert.Equal("cpu", svc.ConfigWidgetIds[0]);
        Assert.Equal("gpu", svc.ConfigWidgetIds[1]);
    }

    [Fact]
    public void GetWidgetSpan_ReturnsStoredSpan()
    {
        var svc = CreateService();
        var entries = new[]
        {
            new WidgetEntry { Id = "profiles", ColSpan = 3, RowSpan = 2 },
        };

        svc.LoadWidgetSpans(entries);

        var (colSpan, rowSpan) = svc.GetWidgetSpan("profiles");
        Assert.Equal(3, colSpan);
        Assert.Equal(2, rowSpan);
    }

    [Fact]
    public void GetWidgetSpan_UnknownId_ReturnsOneOne()
    {
        var svc = CreateService();
        var (colSpan, rowSpan) = svc.GetWidgetSpan("nonexistent");
        Assert.Equal(1, colSpan);
        Assert.Equal(1, rowSpan);
    }

    [Fact]
    public void LoadWidgetSpans_ClearsPreviousData()
    {
        var svc = CreateService();
        svc.LoadWidgetSpans(new[] { new WidgetEntry { Id = "cpu", ColSpan = 1, RowSpan = 1 } });

        svc.LoadWidgetSpans(new[] { new WidgetEntry { Id = "gpu", ColSpan = 2, RowSpan = 1 } });

        Assert.Single(svc.ConfigWidgetIds);
        Assert.Equal("gpu", svc.ConfigWidgetIds[0]);
        Assert.Equal((1, 1), svc.GetWidgetSpan("cpu")); // Old data gone
    }

    [Fact]
    public void ConfigWidgetIds_InitiallyEmpty()
    {
        var svc = CreateService();
        Assert.Empty(svc.ConfigWidgetIds);
    }

    // ===== UpdateLayoutFromGridWidgets =====

    [Fact]
    public void UpdateLayoutFromGridWidgets_SyncsOrderAndSpans()
    {
        var svc = CreateService();
        var widgets = new[]
        {
            ("adaptive", 2, 1),
            ("cpu", 1, 1),
            ("profiles", 3, 2),
        };

        svc.UpdateLayoutFromGridWidgets(widgets);

        Assert.Equal(3, svc.ConfigWidgetIds.Count);
        Assert.Equal("adaptive", svc.ConfigWidgetIds[0]);
        Assert.Equal("cpu", svc.ConfigWidgetIds[1]);
        Assert.Equal("profiles", svc.ConfigWidgetIds[2]);
        Assert.Equal((2, 1), svc.GetWidgetSpan("adaptive"));
        Assert.Equal((3, 2), svc.GetWidgetSpan("profiles"));
    }

    [Fact]
    public void UpdateLayoutFromGridWidgets_ClearsPreviousData()
    {
        var svc = CreateService();
        svc.UpdateLayoutFromGridWidgets(new[] { ("cpu", 1, 1) });

        svc.UpdateLayoutFromGridWidgets(new[] { ("gpu", 2, 1) });

        Assert.Single(svc.ConfigWidgetIds);
        Assert.Equal("gpu", svc.ConfigWidgetIds[0]);
        Assert.Equal((1, 1), svc.GetWidgetSpan("cpu")); // Old data gone
    }
}
