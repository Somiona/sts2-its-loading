using ItsLoading;
using Xunit;

// 主题 cfg 契约回归(纯函数;文件 IO 与 BaseLib 交互不在单测范围):
//   解析 —— BaseLib cfg JSON 的 Theme 键(枚举名,忽略大小写)
//   迁移 —— cfg 有键即优先(旧 txt 只可能更旧,直接删);无键时并入旧 txt 值;
//           无论如何都要落一份带 Theme 键的 cfg

public sealed class ThemeRegistryTests
{
    [Theory]
    [InlineData("{\"Theme\": \"Minespire\"}", LoadingTheme.Minespire)]
    [InlineData("{\"Theme\": \"minespire\"}", LoadingTheme.Minespire)] // 小写(手改/迁移源)
    [InlineData("{\"Theme\": \"Classic\"}", LoadingTheme.Classic)]
    [InlineData("{\"Theme\": \"gachathespire\"}", LoadingTheme.GachaTheSpire)] // 小写(手改/迁移源)
    [InlineData("{\n  \"Theme\": \"Minespire\"\n}", LoadingTheme.Minespire)] // BaseLib WriteIndented 格式
    public void ParseThemeValue_reads_valid(string json, LoadingTheme expected)
        => Assert.Equal(expected, ThemeRegistry.ParseThemeValue(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]                     // 空对象(旧版本遗留的空 cfg)
    [InlineData("{\"Other\": \"x\"}")]
    [InlineData("{\"Theme\": 5}")]          // 非字符串
    [InlineData("{\"Theme\": \"Banana\"}")] // 未知主题(未来移除的主题 id)
    [InlineData("not json at all")]
    public void ParseThemeValue_rejects_invalid(string json)
        => Assert.Null(ThemeRegistry.ParseThemeValue(json));

    [Fact]
    public void Migration_cfg_theme_wins_and_removes_legacy()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: true, LoadingTheme.Minespire, legacyExists: true, legacyValue: "classic");
        Assert.Equal(LoadingTheme.Minespire, plan.Result);
        Assert.False(plan.WriteCfg);
        Assert.True(plan.DeleteLegacyTxt);
    }

    [Fact]
    public void Migration_legacy_value_merges_when_cfg_lacks_theme()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: false, LoadingTheme.Classic, legacyExists: true, legacyValue: "minespire");
        Assert.Equal(LoadingTheme.Minespire, plan.Result);
        Assert.True(plan.WriteCfg);
        Assert.True(plan.DeleteLegacyTxt);
    }

    [Fact]
    public void Migration_default_cfg_when_nothing_exists()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: false, LoadingTheme.Classic, legacyExists: false, legacyValue: null);
        Assert.Equal(LoadingTheme.Classic, plan.Result);
        Assert.True(plan.WriteCfg);
        Assert.False(plan.DeleteLegacyTxt);
    }

    [Fact]
    public void Migration_bad_legacy_value_falls_back_to_default()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: false, LoadingTheme.Classic, legacyExists: true, legacyValue: "garbage");
        Assert.Equal(LoadingTheme.Classic, plan.Result);
        Assert.True(plan.WriteCfg);
        Assert.True(plan.DeleteLegacyTxt);
    }
}
