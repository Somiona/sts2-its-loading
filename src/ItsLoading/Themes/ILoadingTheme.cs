using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

/// <summary>用户可见的启动阶段。数值同时是经典主题显示的阶段序号。</summary>
internal enum BootStage
{
    Workshop = 1,
    Mods = 2,
    Essential = 3,
    OpeningAssets = 4,
    MainMenuAssets = 5,
    Intro = 6,
    Menu = 7,
}

/// <summary>
/// 时间线向主题发布的不可变快照。Overall 是全程进度；Local 是当前阶段进度，
/// 负数表示该阶段没有可测总量、主题应显示不定进度。
/// </summary>
#nullable enable
internal readonly record struct LoadingViewState(
    BootStage Stage,
    float Overall,
    float Local,
    string? Step,
    string? Detail,
    bool ForceDraw)
{
    internal const int StageCount = 7;
    internal bool LocalIndeterminate => Local < 0f;
}
#nullable restore

// ---------------------------------------------------------------- 主题接口
//
// 主题 = 加载指示器的一种呈现,挂在 BootTimeline.Presenter 上:
//   Build   —— Init 里建 UI(直接挂 Root;同步突发期禁 Container/deferred)
//   Present —— 即 Presenter 目标;调用密度 = 真实加载活动密度
//   Retire  —— 菜单就绪 + 2s 停留后由编排层调用;gd splash 的 takeover 不归主题管
// 刻度数学、span 记录全部在 BootTimeline —— 主题只管"长什么样"。
// 实现只有 GdBridgeBar:正常路径桥接帧 0 autoload 节点;首装/版本过渡/
// 旧视图已关闭走晚期托管(同一份 gd 主题代码,见 Themes/boot.gd 与 BootSplash)。

/// <summary>加载指示器主题接口。</summary>
#nullable enable
internal interface ILoadingTheme
{
    /// <summary>建立 UI(在 Init 的 build bar 步骤调用;此时 BootSplash.Install 已完成)。</summary>
    void Build();

    /// <summary>呈现一次完整不可变快照。</summary>
    void Present(LoadingViewState state);

    /// <summary>移除:置死亡标志(挡住移除后仍可能触发的 postfix)+ 释放自身节点。</summary>
    void Retire();
}
#nullable restore

/// <summary>
/// 可选的加载主题。枚举名即持久化值(BaseLib cfg 里存 "Classic"/"Minespire",
/// gd 侧 to_lower 后对应 themes/&lt;小写名&gt;/ 文件夹);新增主题 = 建
/// Themes/&lt;id&gt;/theme.gd + 加枚举值 + settings_ui.json 枚举项本地化键,
/// 其余管线(registry/桥/kit/boot)均已泛化,无需改动。
/// </summary>
public enum LoadingTheme
{
    Classic,
    Minespire,
    GachaTheSpire,
}

/// <summary>
/// 主题注册表与选择。主题值存于 BaseLib 标准配置文件 user://mod_configs/ItsLoading.cfg
/// (JSON {"Theme": "…"},gd 侧 boot.gd 的 _read_theme 读同一文件):
///   · 读永远直读文件(gd 帧 0 / C# ThemeRegistry / BaseLib 下拉框 getter 透传)——
///     绝不依赖 BaseLib 加载,BaseLib 缺席时仅无切换 UI,功能完整
///   · 写只走配置界面(下拉框 setter → TrySet 同步原子写;BaseLib 自身的
///     debounce/退出保存随后幂等重写同值)
///   · Init 的 MigrateToCfg 保证文件从首次启动起就带 Theme 键(自带默认 cfg),
///     并把旧 itsloading_theme.txt 的值并入后删除
/// 不声明 BaseLib 依赖:游戏的依赖是拓扑排序强制(必排我们前面),会破坏
/// load-order #0 调整(见 ItsLoading.EnsureFirstInLoadOrder)。
/// </summary>
#nullable enable
internal static class ThemeRegistry
{
    /// <summary>
    /// BaseLib 规则:文件名 = 配置类根命名空间去特殊字符 + ".cfg"
    /// (ItsLoadingCompat.WaterfallConfig 的根命名空间是 ItsLoading)。
    /// 改配置类命名空间时此处必须同步。
    /// </summary>
    internal const string CfgPath = "user://mod_configs/ItsLoading.cfg";

    /// <summary>旧版循环按钮的选择文件;迁移(MigrateToCfg)后删除。</summary>
    internal const string LegacyTxtPath = "user://itsloading_theme.txt";

    internal const LoadingTheme Default = LoadingTheme.Classic;

    /// <summary>当前主题(直读 cfg;缺失/非法值回默认,任何 IO 异常同)。</summary>
    internal static LoadingTheme Current() =>
        ParseThemeValue(TryReadText(ProjectSettings.GlobalizePath(CfgPath))) ?? Default;

    /// <summary>配置界面写入:同步原子写(保留 cfg 里其他键),避免防抖/强退丢失写入。</summary>
    internal static bool TrySet(LoadingTheme theme)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(CfgPath);
            WriteCfgFile(path, theme, TryReadText(path));
            Log.Warn($"[ItsLoading] theme '{theme}' selected (applies from next launch)");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to persist theme choice: {e}");
            return false;
        }
    }

    /// <summary>
    /// Init 的迁移 + 默认 cfg 步骤(我们 load-order #0,先于 BaseLib 注册):
    /// 保证 cfg 带 Theme 键(缺失就补默认/旧 txt 值,已有其他键则合并),
    /// 旧 txt 并入后删除。BaseLib 随后的 Load() 因此总能读到完整键,不触发
    /// 它的"缺键重存"路径。gd 在帧 0(本步骤之前)读不到 Theme 键时仍会
    /// 回退旧 txt——过渡启动的主题显示因此无缝。
    /// </summary>
    internal static void MigrateToCfg()
    {
        try
        {
            string cfgPath = ProjectSettings.GlobalizePath(CfgPath);
            string? cfgJson = TryReadText(cfgPath);
            LoadingTheme? cfgTheme = ParseThemeValue(cfgJson);
            string legacyPath = ProjectSettings.GlobalizePath(LegacyTxtPath);
            bool legacyExists = File.Exists(legacyPath);
            string? legacyValue = legacyExists ? TryReadText(legacyPath) : null;

            MigrationPlan plan = ResolveMigration(
                cfgTheme.HasValue, cfgTheme ?? Default, legacyExists, legacyValue);
            if (plan.WriteCfg) WriteCfgFile(cfgPath, plan.Result, cfgJson);
            if (plan.DeleteLegacyTxt)
            {
                File.Delete(legacyPath);
                Log.Warn("[ItsLoading] legacy theme file migrated and removed");
            }
            Log.Warn($"[ItsLoading] theme cfg ensured (theme={plan.Result}, " +
                     $"writeCfg={plan.WriteCfg}, legacyRemoved={plan.DeleteLegacyTxt})");
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to migrate theme cfg: {e}");
        }
    }

    // ---- 纯函数(不触 Godot 类型;tests/ 表驱动覆盖) ----

    /// <summary>从 cfg JSON 解析 Theme(枚举名,忽略大小写);缺失/非法/坏 JSON → null。</summary>
    internal static LoadingTheme? ParseThemeValue(string? json)
    {
        try
        {
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Theme", out var el)
                && el.ValueKind == JsonValueKind.String
                && Enum.TryParse<LoadingTheme>(el.GetString(), ignoreCase: true, out var theme)
                ? theme
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal readonly record struct MigrationPlan(LoadingTheme Result, bool WriteCfg, bool DeleteLegacyTxt);

    /// <summary>
    /// 迁移决策:cfg 的 Theme 键优先(有了就直接删 txt——txt 只可能更旧);
    /// 没有则取 txt 值(合法时)或默认,总要写 cfg(保证 cfg 始终带 Theme 键)。
    /// </summary>
    internal static MigrationPlan ResolveMigration(
        bool cfgHasTheme, LoadingTheme cfgValue, bool legacyExists, string? legacyValue)
    {
        if (cfgHasTheme)
            return new MigrationPlan(cfgValue, WriteCfg: false, DeleteLegacyTxt: legacyExists);
        if (legacyExists && Enum.TryParse(legacyValue?.Trim(), ignoreCase: true, out LoadingTheme fromTxt))
            return new MigrationPlan(fromTxt, WriteCfg: true, DeleteLegacyTxt: true);
        return new MigrationPlan(Default, WriteCfg: true, DeleteLegacyTxt: legacyExists);
    }

    // ---- 文件 IO(薄包装;写镜像 BaseLib SaveInternal 的 tmp+Move 原子模式) ----

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading] theme cfg read failed ({e.Message}) — treating as missing");
            return null;
        }
    }

    private static void WriteCfgFile(string path, LoadingTheme theme, string? existingJson)
    {
        // 合并保留其他键(将来加配置项时不互相覆盖);坏 JSON 直接重建
        var values = new Dictionary<string, string>();
        if (existingJson != null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson);
                if (parsed != null) values = parsed;
            }
            catch (JsonException) { }
        }
        values["Theme"] = theme.ToString();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".new";
        using (FileStream stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, values, JsonOptions);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
#nullable restore
