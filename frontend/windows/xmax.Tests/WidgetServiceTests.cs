using XmaX.Models;
using XmaX.Services;
using XmaX.Widgets;

namespace XmaX.Tests;

// ===== Test widget stub =====

/// <summary>
/// Minimal IHomeWidget implementation for testing.
/// </summary>
internal sealed class FakeWidget : IHomeWidget
{
    public string WidgetId { get; }
    public WidgetConfig Config { get; }
    public object Control { get; }
    public string? Title => null;
    public int GetRequiredRows(int availableColumns) => Config.Rows;

    public FakeWidget(string id, object? control = null, WidgetConfig? config = null)
    {
        WidgetId = id;
        Config = config ?? WidgetConfig.TransparentTile;
        Control = control ?? new object();
    }
}

// ===== Tests =====

public class WidgetServiceTests
{
    private static WidgetService CreateService()
    {
        using var pipe = new PipeClient();
        return new WidgetService(pipe);
    }

    // ===== Registration =====

    [Fact]
    public void Register_AddsToOrderAndVisibleList()
    {
        var svc = CreateService();
        var w = new FakeWidget("metrics");

        svc.Register(w);

        Assert.Single(svc.WidgetOrder);
        Assert.Equal("metrics", svc.WidgetOrder[0]);
        Assert.Single(svc.VisibleWidgets);
        Assert.Same(w, svc.VisibleWidgets[0]);
        Assert.True(svc.IsVisible("metrics"));
        Assert.True(svc.IsRegistered("metrics"));
    }

    [Fact]
    public void Register_MultipleWidgets_AppendedInOrder()
    {
        var svc = CreateService();

        svc.Register(new FakeWidget("profiles"));
        svc.Register(new FakeWidget("metrics"));
        svc.Register(new FakeWidget("adaptive"));

        Assert.Equal(3, svc.WidgetOrder.Count);
        Assert.Equal("profiles", svc.WidgetOrder[0]);
        Assert.Equal("metrics", svc.WidgetOrder[1]);
        Assert.Equal("adaptive", svc.WidgetOrder[2]);
        Assert.Equal(3, svc.VisibleWidgets.Count);
    }

    [Fact]
    public void Register_DuplicateId_DoesNotDuplicate()
    {
        var svc = CreateService();
        var w1 = new FakeWidget("metrics");
        var w2 = new FakeWidget("metrics"); // same ID, different instance

        svc.Register(w1);
        svc.Register(w2);

        Assert.Single(svc.WidgetOrder);
        // Second registration replaces the widget in the dictionary
        Assert.Same(w2, svc.GetWidget("metrics"));
    }

    [Fact]
    public void Register_EmptyId_Throws()
    {
        var svc = CreateService();
        var w = new FakeWidget("");

        Assert.Throws<ArgumentException>(() => svc.Register(w));
    }

    [Fact]
    public void Register_NullWidget_Throws()
    {
        var svc = CreateService();

        Assert.Throws<ArgumentNullException>(() => svc.Register(null!));
    }

    // ===== Unregister =====

    [Fact]
    public void Unregister_RemovesFromOrderAndVisibleList()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("metrics"));
        svc.Register(new FakeWidget("profiles"));

        svc.Unregister("metrics");

        Assert.Single(svc.WidgetOrder);
        Assert.Equal("profiles", svc.WidgetOrder[0]);
        Assert.Single(svc.VisibleWidgets);
        Assert.False(svc.IsRegistered("metrics"));
        Assert.False(svc.IsVisible("metrics"));
    }

    [Fact]
    public void Unregister_UnknownId_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("metrics"));

        svc.Unregister("nonexistent");

        Assert.Single(svc.WidgetOrder);
    }

    // ===== Visibility =====

    [Fact]
    public void SetVisible_False_RemovesFromVisibleList()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("profiles"));
        svc.Register(new FakeWidget("metrics"));

        svc.SetVisible("metrics", false);

        Assert.Equal(2, svc.WidgetOrder.Count);       // Still in order
        Assert.Single(svc.VisibleWidgets);              // But not in visible list
        Assert.Equal("profiles", svc.VisibleWidgets[0].WidgetId);
        Assert.False(svc.IsVisible("metrics"));
    }

    [Fact]
    public void SetVisible_True_RestoresToVisibleList()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("profiles"));
        svc.Register(new FakeWidget("metrics"));
        svc.SetVisible("metrics", false);

        svc.SetVisible("metrics", true);

        Assert.Equal(2, svc.VisibleWidgets.Count);
        Assert.True(svc.IsVisible("metrics"));
    }

    [Fact]
    public void SetVisible_SameValue_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("metrics"));

        var changed = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Visibility[metrics]") changed = true;
        };

        // Already visible — setting visible again should not fire
        svc.SetVisible("metrics", true);
        Assert.False(changed);
    }

    [Fact]
    public void SetVisible_UnknownWidget_NoOp()
    {
        var svc = CreateService();

        svc.SetVisible("nonexistent", false); // Should not throw
        Assert.False(svc.IsVisible("nonexistent"));
    }

    [Fact]
    public void ToggleVisible_FlipsState()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("metrics"));
        Assert.True(svc.IsVisible("metrics"));

        svc.ToggleVisible("metrics");
        Assert.False(svc.IsVisible("metrics"));

        svc.ToggleVisible("metrics");
        Assert.True(svc.IsVisible("metrics"));
    }

    // ===== Reordering =====

    [Fact]
    public void MoveUp_MovesOnePositionEarlier()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        svc.Register(new FakeWidget("c"));

        svc.MoveUp("b");

        Assert.Equal("b", svc.WidgetOrder[0]);
        Assert.Equal("a", svc.WidgetOrder[1]);
        Assert.Equal("c", svc.WidgetOrder[2]);
    }

    [Fact]
    public void MoveUp_AlreadyFirst_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));

        svc.MoveUp("a"); // Already first

        Assert.Equal("a", svc.WidgetOrder[0]);
        Assert.Equal("b", svc.WidgetOrder[1]);
    }

    [Fact]
    public void MoveUp_UnknownId_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));

        svc.MoveUp("nonexistent"); // Should not throw

        Assert.Single(svc.WidgetOrder);
    }

    [Fact]
    public void MoveDown_MovesOnePositionLater()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        svc.Register(new FakeWidget("c"));

        svc.MoveDown("b");

        Assert.Equal("a", svc.WidgetOrder[0]);
        Assert.Equal("c", svc.WidgetOrder[1]);
        Assert.Equal("b", svc.WidgetOrder[2]);
    }

    [Fact]
    public void MoveDown_AlreadyLast_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));

        svc.MoveDown("b"); // Already last

        Assert.Equal("a", svc.WidgetOrder[0]);
        Assert.Equal("b", svc.WidgetOrder[1]);
    }

    [Fact]
    public void MoveDown_UnknownId_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));

        svc.MoveDown("nonexistent"); // Should not throw

        Assert.Single(svc.WidgetOrder);
    }

    [Fact]
    public void SetOrder_ReordersWidgets()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        svc.Register(new FakeWidget("c"));

        svc.SetOrder(new[] { "c", "a", "b" });

        Assert.Equal("c", svc.WidgetOrder[0]);
        Assert.Equal("a", svc.WidgetOrder[1]);
        Assert.Equal("b", svc.WidgetOrder[2]);
    }

    [Fact]
    public void SetOrder_UnknownIdsAreIgnored_AppendedAtEnd()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));

        svc.SetOrder(new[] { "nonexistent", "b", "a" });

        // "nonexistent" skipped, "b" and "a" in specified order
        Assert.Equal("b", svc.WidgetOrder[0]);
        Assert.Equal("a", svc.WidgetOrder[1]);
    }

    [Fact]
    public void SetOrder_MissingRegisteredWidgetsAppendedAtEnd()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        svc.Register(new FakeWidget("c"));

        svc.SetOrder(new[] { "c" }); // a and b not mentioned

        Assert.Equal("c", svc.WidgetOrder[0]);
        // a and b appended in their original relative order
        Assert.Contains("a", svc.WidgetOrder);
        Assert.Contains("b", svc.WidgetOrder);
    }

    // ===== Column count =====

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

    // ===== LoadLayout / GetLayout =====

    [Fact]
    public void GetLayout_ReturnsCurrentState()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        svc.SetVisible("b", false);
        svc.Columns = 4;

        var layout = svc.GetLayout();

        Assert.Equal(new[] { "a", "b" }, layout.WidgetOrder);
        Assert.True(layout.WidgetVisibility["a"]);
        Assert.False(layout.WidgetVisibility["b"]);
        Assert.Equal(4, layout.Columns);
    }

    [Fact]
    public void LoadLayout_AppliesOrderVisibilityAndColumns()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        svc.Register(new FakeWidget("c"));

        var layout = new HomeLayout
        {
            WidgetOrder = new List<string> { "c", "a", "b" },
            WidgetVisibility = new Dictionary<string, bool>
            {
                ["b"] = false,
            },
            Columns = 5,
        };

        svc.LoadLayout(layout);

        Assert.Equal("c", svc.WidgetOrder[0]);
        Assert.Equal("a", svc.WidgetOrder[1]);
        Assert.Equal("b", svc.WidgetOrder[2]);
        Assert.False(svc.IsVisible("b"));
        Assert.True(svc.IsVisible("a")); // Default visibility unchanged
        Assert.Equal(5, svc.Columns);
        Assert.Equal(2, svc.VisibleWidgets.Count); // b is hidden
    }

    [Fact]
    public void LoadLayout_UnknownIdsInOrder_AreIgnored()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));

        var layout = new HomeLayout
        {
            WidgetOrder = new List<string> { "nonexistent", "a" },
        };

        svc.LoadLayout(layout);

        // "nonexistent" is not a registered widget, so only "a" remains
        Assert.Single(svc.WidgetOrder);
        Assert.Equal("a", svc.WidgetOrder[0]);
    }

    [Fact]
    public void LoadLayout_NullLayout_NoOp()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));

        svc.LoadLayout(null!); // Should not throw

        Assert.Single(svc.WidgetOrder);
    }

    // ===== VisibleWidgets tracks changes =====

    [Fact]
    public void VisibleWidgets_UpdatesOnRegisterUnregisterMoveToggle()
    {
        var svc = CreateService();

        // Register
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));
        Assert.Equal(2, svc.VisibleWidgets.Count);

        // Hide
        svc.SetVisible("a", false);
        Assert.Single(svc.VisibleWidgets);
        Assert.Equal("b", svc.VisibleWidgets[0].WidgetId);

        // Move b up (no visible change since a is hidden)
        svc.MoveUp("b");
        Assert.Single(svc.VisibleWidgets);

        // Show a
        svc.SetVisible("a", true);
        Assert.Equal(2, svc.VisibleWidgets.Count);
        // b was moved up, so b is first
        Assert.Equal("b", svc.VisibleWidgets[0].WidgetId);
        Assert.Equal("a", svc.VisibleWidgets[1].WidgetId);

        // Unregister b
        svc.Unregister("b");
        Assert.Single(svc.VisibleWidgets);
        Assert.Equal("a", svc.VisibleWidgets[0].WidgetId);
    }

    // ===== PropertyChanged =====

    [Fact]
    public void PropertyChanged_FiresOnMove()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("a"));
        svc.Register(new FakeWidget("b"));

        var firedProperties = new List<string>();
        svc.PropertyChanged += (_, e) => firedProperties.Add(e.PropertyName!);

        svc.MoveUp("b");

        Assert.Contains(nameof(WidgetService.WidgetOrder), firedProperties);
        Assert.Contains(nameof(WidgetService.VisibleWidgets), firedProperties);
    }

    [Fact]
    public void PropertyChanged_FiresOnVisibilityChange()
    {
        var svc = CreateService();
        svc.Register(new FakeWidget("metrics"));

        var firedProperties = new List<string>();
        svc.PropertyChanged += (_, e) => firedProperties.Add(e.PropertyName!);

        svc.SetVisible("metrics", false);

        Assert.Contains("Visibility[metrics]", firedProperties);
        Assert.Contains(nameof(WidgetService.VisibleWidgets), firedProperties);
    }
}
