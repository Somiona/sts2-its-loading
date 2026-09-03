using System;
using ItsLoading;
using Xunit;

public sealed class WindowsLayerSurfaceTests
{
    [Fact]
    public void Log_windows_match_the_native_theme_semantics()
    {
        string[] entries = { "a", "b", "c", "d", "e", "f", "g" };

        Assert.Equal("c", WindowsLayerSurface.LogColumnLine(entries, 0, 5));
        Assert.Equal("d | e", WindowsLayerSurface.LogRowsLine(entries, 0, 2, 2, " | "));
        Assert.Equal("f | g", WindowsLayerSurface.LogRowsLine(entries, 1, 2, 2, " | "));
    }

    [Fact]
    public void Indeterminate_motion_stays_inside_its_track()
    {
        var slide = new IndeterminateDef { Mode = IndeterminateMode.Slide, CycleS = 3 };
        var pulse = new IndeterminateDef { Mode = IndeterminateMode.Pulse, MinW = 10, Travel = 30 };

        foreach (double t in new[] { 0d, .5, 1.25, 2.99, 10 })
        {
            var s = WindowsLayerSurface.Indeterminate(100, slide, t);
            var p = WindowsLayerSurface.Indeterminate(100, pulse, t);
            Assert.InRange(s.Offset, 0, 100 - s.Width);
            Assert.InRange(p.Offset, 0, 100 - p.Width);
            Assert.InRange(s.Width, 1, 100);
            Assert.InRange(p.Width, 1, 100);
        }
    }
}
