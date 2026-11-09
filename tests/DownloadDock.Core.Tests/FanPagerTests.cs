using DownloadDock.Core.Fan;

namespace DownloadDock.Core.Tests;

public class FanPagerTests
{
    [Fact]
    public void Scrolls_through_older_files_and_clamps()
    {
        var p = new FanPager();
        p.Update(totalCount: 25, capacity: 9);
        Assert.Equal((0, 9), p.VisibleRange);
        Assert.Equal(16, p.HiddenOlder);
        Assert.False(p.CanScrollNewer);

        Assert.True(p.Scroll(5));
        Assert.Equal((5, 9), p.VisibleRange);
        Assert.Equal(5, p.HiddenNewer);

        Assert.True(p.Scroll(100));
        Assert.Equal(16, p.Offset);
        Assert.False(p.CanScrollOlder);
        Assert.False(p.Scroll(1));

        Assert.True(p.Scroll(-100));
        Assert.Equal(0, p.Offset);
    }

    [Fact]
    public void Fewer_files_than_capacity_never_scroll()
    {
        var p = new FanPager();
        p.Update(4, 9);
        Assert.False(p.Scroll(1));
        Assert.Equal((0, 4), p.VisibleRange);
        Assert.Equal(0, p.HiddenOlder);
    }

    [Fact]
    public void Shrinking_list_reclamps_offset()
    {
        var p = new FanPager();
        p.Update(30, 10);
        p.Scroll(20);
        p.Update(12, 10);
        Assert.Equal(2, p.Offset);
    }
}
