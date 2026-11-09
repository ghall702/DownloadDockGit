using DownloadDock.Core.Fan;

namespace DownloadDock.Core.Tests;

public class FanLayoutTests
{
    // 1920×1080 monitor, 48 px taskbar, tray icon ~330 px from the right edge.
    private static readonly RectD Work = new(0, 0, 1920, 1032);

    private static FanLayoutResult Up(int count, double anchorX = 1590, RectD? work = null, FanLayoutOptions? o = null)
        => FanLayoutCalculator.Calculate(count, anchorX, (work ?? Work).Bottom, work ?? Work, FanDirection.Up, o);

    [Fact]
    public void Default_fan_fits_ten_items_on_a_1080p_screen()
    {
        var r = Up(10);
        Assert.Equal(10, r.FittedCount);
        Assert.Equal(10, r.Items.Count);
        Assert.False(r.Mirrored);
    }

    [Fact]
    public void Items_rise_from_the_anchor_and_curve_with_increasing_rotation()
    {
        var r = Up(9);
        var items = r.Items;
        // First item sits just above the anchor, horizontally on it.
        Assert.Equal(1590, items[0].CenterX, 3);
        Assert.True(items[0].CenterY < Work.Bottom);
        for (var i = 1; i < items.Count; i++)
        {
            Assert.True(items[i].CenterY < items[i - 1].CenterY, "each item is higher than the previous one");
            Assert.True(items[i].CenterX > items[i - 1].CenterX, "the arc bends to the right like the macOS stack");
            Assert.True(items[i].AngleDegrees > items[i - 1].AngleDegrees, "rotation increases along the arc");
        }
        Assert.Equal(0, items[0].AngleDegrees, 6);
        Assert.InRange(items[^1].AngleDegrees, 15, 30);
    }

    [Fact]
    public void Curve_is_non_linear_like_an_arc()
    {
        var items = Up(9).Items;
        var firstStep = items[1].CenterX - items[0].CenterX;
        var lastStep = items[^1].CenterX - items[^2].CenterX;
        Assert.True(lastStep > firstStep * 3, $"horizontal drift should accelerate ({firstStep} vs {lastStep})");
    }

    [Fact]
    public void Neighbouring_thumbnails_overlap()
    {
        var o = new FanLayoutOptions();
        var items = Up(9, o: o).Items;
        for (var i = 1; i < items.Count; i++)
        {
            var d = Math.Sqrt(Math.Pow(items[i].CenterX - items[i - 1].CenterX, 2) + Math.Pow(items[i].CenterY - items[i - 1].CenterY, 2));
            Assert.True(d < o.ThumbnailSize, "consecutive cards overlap");
            Assert.True(d > o.ThumbnailSize * 0.5, "but stay individually selectable");
        }
    }

    [Fact]
    public void Items_closest_to_the_anchor_are_on_top()
    {
        var items = Up(8).Items;
        for (var i = 1; i < items.Count; i++)
            Assert.True(items[i].ZIndex < items[i - 1].ZIndex);
    }

    [Fact]
    public void Fan_stays_within_the_work_area()
    {
        var o = new FanLayoutOptions();
        foreach (var anchorX in new[] { 1590.0, 1800, 1870, 1900, 400 })
        {
            var r = Up(10, anchorX, o: o);
            foreach (var item in r.Items)
            {
                var b = FanLayoutCalculator.ItemBounds(item, o);
                Assert.True(b.Top >= Work.Top - 0.5, $"top inside for x={anchorX}");
                Assert.True(b.Right <= Work.Right + 0.5, $"right inside for x={anchorX} (item {item.Index}: {b.Right})");
                Assert.True(b.Bottom <= Work.Bottom + 0.5, $"bottom inside for x={anchorX}");
            }
        }
    }

    [Fact]
    public void Icon_near_the_right_edge_flattens_or_mirrors_the_curve()
    {
        var normal = Up(10, 1590);
        var tight = Up(10, 1880);
        Assert.True(tight.Mirrored || tight.SweepDegrees < normal.SweepDegrees);
        if (tight.Mirrored)
            Assert.True(tight.Items[^1].CenterX < tight.Items[0].CenterX);
    }

    [Fact]
    public void Too_many_items_for_a_short_screen_are_reduced()
    {
        var shortWork = new RectD(0, 0, 1366, 720);
        var r = Up(20, 1100, shortWork);
        Assert.True(r.FittedCount < 20);
        Assert.True(r.FittedCount >= 5);
        var top = r.Items.Min(i => FanLayoutCalculator.ItemBounds(i, new FanLayoutOptions()).Top);
        Assert.True(top >= shortWork.Top - 0.5);
    }

    [Fact]
    public void Larger_thumbnails_fit_fewer_items()
    {
        var small = Up(20, o: new FanLayoutOptions { ThumbnailSize = 64 });
        var large = Up(20, o: new FanLayoutOptions { ThumbnailSize = 160 });
        Assert.True(large.FittedCount < small.FittedCount);
    }

    [Fact]
    public void Downward_fan_for_top_taskbar()
    {
        var work = new RectD(0, 48, 1920, 1032);
        var r = FanLayoutCalculator.Calculate(8, 1590, work.Top, work, FanDirection.Down);
        for (var i = 1; i < r.Items.Count; i++)
            Assert.True(r.Items[i].CenterY > r.Items[i - 1].CenterY);
        Assert.True(r.Items.All(i => i.CenterY > work.Top));
    }

    [Fact]
    public void Bounds_contain_every_item_and_the_anchor()
    {
        var o = new FanLayoutOptions();
        var r = Up(9, o: o);
        foreach (var item in r.Items)
            Assert.True(r.Bounds.Contains(FanLayoutCalculator.ItemBounds(item, o)));
        Assert.True(r.Bounds.Left <= 1590 && r.Bounds.Right >= 1590 && r.Bounds.Bottom >= Work.Bottom);
    }

    [Fact]
    public void Single_item_and_zero_items_are_handled()
    {
        Assert.Single(Up(1).Items);
        Assert.Empty(Up(0).Items);
    }

    [Fact]
    public void High_dpi_work_area_in_dips_behaves_like_any_other()
    {
        // 3840×2160 at 200 % = 1920×1080 DIPs.
        var work = new RectD(1920, 0, 1920, 1056);
        var r = FanLayoutCalculator.Calculate(10, 1920 + 1600, work.Bottom, work, FanDirection.Up);
        Assert.Equal(10, r.FittedCount);
        Assert.True(r.Items.All(i => i.CenterX > 1920));
    }
}
