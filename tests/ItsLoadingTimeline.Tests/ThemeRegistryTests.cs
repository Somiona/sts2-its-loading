using ItsLoading;
using Xunit;

// 主题 cfg 契约回归(纯函数;文件 IO 与 BaseLib 交互不在单测范围):
//   解析 —— Theme id(字符串,统一小写;旧枚举名 "Minespire" 归一化兼容)
//   迁移 —— cfg 有键即优先(旧 txt 只可能更旧,直接删);无键时并入旧 txt 值;
//           无论如何都要落一份带 Theme 键的 cfg
//   id 合法形 —— 小写字母/数字/-/_(即文件夹名约束,ThemePacks 同款)

public sealed class ThemeRegistryTests
{
    [Theory]
    [InlineData("{\"Theme\": \"minespire\"}", "minespire")]
    [InlineData("{\"Theme\": \"Minespire\"}", "minespire")]        // 旧枚举名(存量 cfg)
    [InlineData("{\"Theme\": \"GachaTheSpire\"}", "gachathespire")] // 旧枚举名
    [InlineData("{\"Theme\": \"my-pack-2\"}", "my-pack-2")]         // 外部包 id 形
    [InlineData("{\n  \"Theme\": \"classic\"\n}", "classic")]     // WriteIndented 格式
    public void ParseThemeValue_reads_valid(string json, string expected)
        => Assert.Equal(expected, ThemeRegistry.ParseThemeValue(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"Other\": \"x\"}")]
    [InlineData("{\"Theme\": 5}")]
    [InlineData("{\"Theme\": \"Banana!\"}")]  // 非法 id 形(大写/特殊字符)
    [InlineData("not json at all")]
    public void ParseThemeValue_rejects_invalid(string json)
        => Assert.Null(ThemeRegistry.ParseThemeValue(json));

    [Fact]
    public void Migration_cfg_theme_wins_and_removes_legacy()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: true, "minespire", legacyExists: true, legacyValue: "classic");
        Assert.Equal("minespire", plan.Result);
        Assert.False(plan.WriteCfg);
        Assert.True(plan.DeleteLegacyTxt);
    }

    [Fact]
    public void Migration_legacy_value_merges_when_cfg_lacks_theme()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: false, ThemeRegistry.Default, legacyExists: true, legacyValue: "Minespire");
        Assert.Equal("minespire", plan.Result); // 旧枚举名归一化
        Assert.True(plan.WriteCfg);
        Assert.True(plan.DeleteLegacyTxt);
    }

    [Fact]
    public void Migration_default_cfg_when_nothing_exists()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: false, ThemeRegistry.Default, legacyExists: false, legacyValue: null);
        Assert.Equal("classic", plan.Result);
        Assert.True(plan.WriteCfg);
        Assert.False(plan.DeleteLegacyTxt);
    }

    [Fact]
    public void Migration_bad_legacy_value_falls_back_to_default()
    {
        var plan = ThemeRegistry.ResolveMigration(
            cfgHasTheme: false, ThemeRegistry.Default, legacyExists: true, legacyValue: "garbage!!");
        Assert.Equal("classic", plan.Result);
        Assert.True(plan.WriteCfg);
        Assert.True(plan.DeleteLegacyTxt);
    }

    // (Beta)原生加载屏渲染器开关:cfg 布尔键;缺失/非法 → null(语义默认开)。
    // 双格式:真布尔(我们的写入)+ 字符串 "True"/"False"(BaseLib 规范格式)

    [Theory]
    [InlineData("{\"NativeRenderer\": false}", false)]
    [InlineData("{\"NativeRenderer\": true}", true)]
    [InlineData("{\"NativeRenderer\": \"False\"}", false)]   // BaseLib 写入格式
    [InlineData("{\"NativeRenderer\": \"True\"}", true)]
    [InlineData("{\"NativeRenderer\": \"false\"}", false)]   // 小写也认(bool.TryParse 忽略大小写)
    [InlineData("{\n  \"NativeRenderer\": \"True\"\n}", true)] // WriteIndented 格式
    [InlineData("{\"Theme\": \"classic\", \"NativeRenderer\": \"False\"}", false)] // 与 Theme 键共存
    public void ParseNativeRendererValue_reads_valid(string json, bool expected)
        => Assert.Equal(expected, ThemeRegistry.ParseNativeRendererValue(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]                        // 缺键 = 默认开
    [InlineData("{\"Theme\": \"classic\"}")]
    [InlineData("{\"NativeRenderer\": \"yes\"}")]
    [InlineData("{\"NativeRenderer\": 1}")]
    [InlineData("not json at all")]
    public void ParseNativeRendererValue_rejects_invalid(string json)
        => Assert.Null(ThemeRegistry.ParseNativeRendererValue(json));

    // (Debug)开发者标定视图:同双格式;缺失 → null(语义默认关)

    [Theory]
    [InlineData("{\"CalibView\": \"True\"}", true)]     // BaseLib 写入格式
    [InlineData("{\"CalibView\": false}", false)]
    [InlineData("{\"CalibView\": \"false\"}", false)]   // 小写也认
    public void ParseCalibViewValue_reads_valid(string json, bool expected)
        => Assert.Equal(expected, ThemeRegistry.ParseCalibViewValue(json));

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"CalibView\": \"yes\"}")]
    public void ParseCalibViewValue_rejects_invalid(string json)
        => Assert.Null(ThemeRegistry.ParseCalibViewValue(json));
}
