using Starlight.Overlay;

namespace Starlight.Tests;

public sealed class OverlayGeometryTests
{
    // The user's setup: two 1080p monitors at 100 %, plus cases for 125 % and a monitor left of the main one.
    private static readonly MonitorInfo Main = new(0, 0, 1920, 1080, 1.0, true, "main");
    private static readonly MonitorInfo Left125 = new(-2400, -200, 2400, 1350, 1.25, false, "left");

    [Fact]
    public void Points_convert_to_local_units_per_monitor()
    {
        Assert.Equal((100, 50), OverlayGeometry.ToLocal(Main, 100, 50));
        Assert.Equal((400, 240), OverlayGeometry.ToLocal(Left125, -1900, 100));
    }

    [Fact]
    public void Finds_the_monitor_for_a_point_or_the_nearest()
    {
        MonitorInfo[] all = [Main, Left125];
        Assert.Equal("left", OverlayGeometry.MonitorFor(all, -10, 10).Name);
        Assert.Equal("main", OverlayGeometry.MonitorFor(all, 5000, 10).Name);
    }

    [Fact]
    public void Orb_sits_top_centre_of_the_primary_monitor()
    {
        Assert.Equal((960, 64), OverlayGeometry.OrbCenter([Left125, Main], 96, 16));
    }

    [Fact]
    public void A_pointer_line_is_split_across_monitors()
    {
        // From the orb on the main monitor to a button on the left monitor.
        var onMain = OverlayGeometry.ClipLine(Main, 960, 64, -1200, 400);
        var onLeft = OverlayGeometry.ClipLine(Left125, 960, 64, -1200, 400);
        Assert.NotNull(onMain);
        Assert.NotNull(onLeft);
        Assert.Equal(0, onMain.Value.X2, 1);
        Assert.Equal(1920, onLeft.Value.X1, 1); // the left monitor's right edge, at 125 % (2400 px / 1.25)
        Assert.Equal(960, onLeft.Value.X2, 1);
        Assert.Null(OverlayGeometry.ClipLine(Left125, 100, 100, 900, 900));
    }
}
