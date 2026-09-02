using System.IO;
using System.Linq;
using ItsLoading;
using Xunit;

#nullable enable

public sealed class ThemePlanTests
{
    [Fact]
    public void Gacha_plan_resolves_assets_defaults_and_shared_geometry()
    {
        string dir = Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "gachathespire");
        var warnings = new System.Collections.Generic.List<string>();
        ThemePlan? plan = ThemeCompiler.Compile(dir, warnings.Add);

        Assert.NotNull(plan);
        Assert.Empty(warnings);
        Assert.True(plan!.SupportsNative);
        var row1 = plan.Rows["row1"];
        Assert.Equal("gachathespire_1.png", row1.Sources[0]);
        Assert.Equal("gachathespire_7.png", row1.Sources[6]);
        Assert.Equal(7, row1.Slots.Select(r => r.X).Distinct().Count());
        Assert.Equal(213, row1.Bounds.X, 3);
        Assert.Equal(286, row1.Bounds.Y, 3);

        var row2 = plan.Rows["row2"];
        Assert.Equal("gachathespire_8.png", row2.Sources[0]);
        Assert.Equal("gachathespire_14.png", row2.Sources[6]);
        Assert.Equal(row2.Bounds, plan.Masks["mask"].Domain);
        Assert.All(plan.DotSets["dots"].Dots,
            dot => Assert.Equal(366, dot.Y + dot.H / 2, 3));

        var log = Assert.IsType<LogRowsElement>(plan.Elements.Single(e => e.Id == "log"));
        Assert.Equal(1, log.Align);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ItsLoading", "themes")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
