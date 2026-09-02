using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

// ---------------------------------------------------------------- Godot 呈现 adapter
//
// Godot adapter 经 SurfaceRouter 消费 LoadingFrame:
//   Present —— 唯一动态输入;调用密度 = 真实加载活动密度
//   Retire  —— 菜单就绪 + 2s 停留后由编排层调用;gd splash 的 takeover 不归主题管
// 刻度数学、span 记录全部在 BootTimeline —— 主题只管"长什么样"。
// 实现只有 GdBridgeBar:正常路径桥接帧 0 autoload 节点;首装/版本过渡/
// 旧视图已关闭走晚期托管(同一份 gd 主题代码,见 render/boot.gd 与 BootSplash)。

/// <summary>已挂载的 Godot 基础呈现面；主题本身由 theme.json 定义。</summary>
#nullable enable
internal interface IGodotSurface
{
    /// <summary>呈现一次完整不可变帧。</summary>
    void Present(LoadingFrame frame);

    /// <summary>切换 standby 可见性；不销毁节点，供 native 接管与恢复。</summary>
    void SetVisible(bool visible);

    /// <summary>移除:置死亡标志(挡住移除后仍可能触发的 postfix)+ 释放自身节点。</summary>
    void Retire();
}
#nullable restore

/// <summary>
/// 主题 id 即 themes/&lt;id&gt;/ 文件夹名(小写;cfg 持久化同值,旧枚举名
/// "Minespire" 读入时归一化)。内置主题随 mod 发行;外部主题包 = 普通 mod
/// 携带 themes/&lt;id&gt;/(发现与缓存见 ThemePacks)。新增内置主题 = 建
/// themes/&lt;id&gt;/theme.json,零代码改动。
/// </summary>
/// <summary>
/// 主题选择与持久化。值 = 主题 id(字符串),存于 BaseLib 标准配置文件
/// user://mod_configs/ItsLoading.cfg(JSON {"Theme": "minespire"},gd 侧
/// boot.gd 的 _read_theme 读同一文件):
///   · 读永远直读文件(gd 帧 0 / C# ThemeRegistry / 画廊应用)—— 绝不依赖
///     BaseLib 加载,BaseLib 缺席时仅无入口 UI,功能完整
///   · 写只走画廊应用(TrySet 同步原子写)
///   · Init 的 MigrateToCfg 保证文件从首次启动起就带 Theme 键(默认 classic),
///     并把旧 itsloading_theme.txt(及旧枚举名大小写)并入后删除/归一化
/// 可用主题集(内置 + 外部包)见 ThemePacks;未知 id 由装载链回 classic。
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

    /// <summary>主题值独占文件(Inc 8):BaseLib 拥有 ItsLoading.cfg 并按它的
    /// 属性表整体重写 —— Theme 从配置类退役后,留在那里的 Theme 键会被 BaseLib
    /// 的保存直接抹掉(实测)。主题值只能放我们独占的兄弟文件。</summary>
    internal const string ThemeCfgPath = "user://mod_configs/ItsLoading.theme.cfg";

    /// <summary>旧版循环按钮的选择文件;迁移(MigrateToCfg)后删除。</summary>
    internal const string LegacyTxtPath = "user://itsloading_theme.txt";

    internal const string Default = "classic";

    /// <summary>主题 id 合法形(小写字母/数字/-/_;即文件夹名约束)。</summary>
    internal static bool IsValidId(string? id) =>
        id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9_-]+$");

    /// <summary>当前主题 id(直读独占文件;过渡启动回退旧 cfg 键;任何 IO 异常回默认)。</summary>
    internal static string Current() =>
        ParseThemeValue(TryReadText(ProjectSettings.GlobalizePath(ThemeCfgPath)))
        ?? ParseThemeValue(TryReadText(ProjectSettings.GlobalizePath(CfgPath)))
        ?? Default;

    /// <summary>画廊应用:同步原子写(保留 cfg 里其他键),避免防抖/强退丢失写入。</summary>
    internal static bool TrySet(string id)
    {
        if (!IsValidId(id)) return false;
        try
        {
            string? themeDir = ThemePacks.DirOf(id);
            if (themeDir == null)
            {
                Log.Warn($"[ItsLoading] theme '{id}' is not installed");
                return false;
            }
            ThemePlan? plan = ThemeCompiler.Compile(themeDir, warning => Log.Warn(warning));
            if (plan == null)
            {
                Log.Warn($"[ItsLoading] theme '{id}' did not compile; selection unchanged");
                return false;
            }
            if (!plan.SupportsNative)
                Log.Warn($"[ItsLoading] theme '{id}' is Godot-only: "
                    + string.Join("; ", plan.NativeIncompatibilities));
            string path = ProjectSettings.GlobalizePath(ThemeCfgPath);
            WriteCfgValue(path, "Theme", id, TryReadText(path));
            Log.Warn($"[ItsLoading] theme '{id}' selected (applies from next launch)");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to persist theme choice: {e}");
            return false;
        }
    }

    /// <summary>
    /// (Beta)原生加载屏渲染器开关(默认开)。关 = 全程走 Godot 基础路径:
    /// 不向 SurfaceRouter 提供 native factory,冻结期也保持 Godot 基础路径 —— 原生路径的整体逃生舱,
    /// 排障时可一键回到改动前行为。仅 C# 消费(gd 侧永远不需要)。
    /// </summary>
    internal static bool NativeRendererEnabled() =>
        ParseNativeRendererValue(TryReadText(ProjectSettings.GlobalizePath(CfgPath))) ?? true;

    /// <summary>设置界面写入:同步原子写(保留其他键;与 Theme 键共存互不覆盖)。</summary>
    internal static bool TrySetNativeRenderer(bool on)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(CfgPath);
            WriteCfgValue(path, "NativeRenderer", on, TryReadText(path));
            Log.Warn($"[ItsLoading] native renderer {(on ? "enabled" : "disabled")} " +
                     "(applies from next launch)");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to persist native renderer choice: {e}");
            return false;
        }
    }

    /// <summary>
    /// (Debug)开发者标定视图(默认关):双渲染器同规则的品红元素框 + 10% 网格
    /// (原生面另带画布边框)—— 布局比对/主题开发用。gd 与 C# 都直读本键。
    /// </summary>
    internal static bool CalibViewEnabled() =>
        ParseCalibViewValue(TryReadText(ProjectSettings.GlobalizePath(CfgPath))) ?? false;

    internal static bool TrySetCalibView(bool on)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(CfgPath);
            WriteCfgValue(path, "CalibView", on, TryReadText(path));
            Log.Warn($"[ItsLoading] calibration view {(on ? "on" : "off")} (next launch)");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to persist calibration view: {e}");
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
            // 主题值迁入独占文件:优先级 独占文件 > 旧 cfg 键(过渡)> 旧 txt > 默认
            string themePath = ProjectSettings.GlobalizePath(ThemeCfgPath);
            string? cfgJson = TryReadText(themePath);
            if (ParseThemeValue(cfgJson) == null)
                cfgJson ??= TryReadText(ProjectSettings.GlobalizePath(CfgPath));
            string? cfgTheme = ParseThemeValue(cfgJson);
            string legacyPath = ProjectSettings.GlobalizePath(LegacyTxtPath);
            bool legacyExists = File.Exists(legacyPath);
            string? legacyValue = legacyExists ? TryReadText(legacyPath) : null;

            MigrationPlan plan = ResolveMigration(
                cfgTheme != null, cfgTheme ?? Default, legacyExists, legacyValue);
            if (plan.WriteCfg) WriteCfgValue(themePath, "Theme", plan.Result, cfgJson);
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

    /// <summary>
    /// 从 cfg JSON 解析 NativeRenderer;缺失/坏 JSON → null(语义默认开)。
    /// 双格式:真布尔(我们自己的写入)与字符串 "True"/"False"(BaseLib 的
    /// 规范格式 —— 它把 bool 序列化成 ToString(),读不回真布尔,会回退默认值)。
    /// </summary>
    internal static bool? ParseNativeRendererValue(string? json)
    {
        try
        {
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("NativeRenderer", out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 cfg JSON 解析 CalibView(双格式,同 NativeRenderer);缺失 → null(默认关)。</summary>
    internal static bool? ParseCalibViewValue(string? json)
    {
        try
        {
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("CalibView", out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 cfg JSON 解析 Theme id(统一小写;兼容旧枚举名 "Minespire");
    /// 缺失/非法 id 形/坏 JSON → null。</summary>
    internal static string? ParseThemeValue(string? json)
    {
        try
        {
            if (json == null) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Theme", out var el)
                || el.ValueKind != JsonValueKind.String) return null;
            string id = el.GetString()!.Trim().ToLowerInvariant();
            return IsValidId(id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal readonly record struct MigrationPlan(string Result, bool WriteCfg, bool DeleteLegacyTxt);

    /// <summary>
    /// 迁移决策:cfg 的 Theme 键优先(有了就直接删 txt——txt 只可能更旧);
    /// 没有则取 txt 值(合法 id 形时)或默认,总要写 cfg(保证 cfg 始终带 Theme 键)。
    /// </summary>
    internal static MigrationPlan ResolveMigration(
        bool cfgHasTheme, string cfgValue, bool legacyExists, string? legacyValue)
    {
        if (cfgHasTheme)
            return new MigrationPlan(cfgValue, WriteCfg: false, DeleteLegacyTxt: legacyExists);
        string? fromTxt = legacyValue?.Trim().ToLowerInvariant();
        if (legacyExists && IsValidId(fromTxt))
            return new MigrationPlan(fromTxt!, WriteCfg: true, DeleteLegacyTxt: true);
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

    /// <summary>
    /// cfg 单键合并原子写(JsonNode 合并,坏 JSON 直接重建)。
    /// 布尔必须写成 BaseLib 的规范字符串格式 "True"/"False" —— 它读不回
    /// 真布尔(会当缺键回退默认值再整体重存),其余 mod 的 cfg 同证。
    /// </summary>
    private static void WriteCfgValue(string path, string key, object value, string? existingJson)
    {
        JsonObject obj;
        try
        {
            obj = existingJson != null && JsonNode.Parse(existingJson) is JsonObject parsed
                ? parsed
                : new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }
        obj[key] = JsonValue.Create(value switch
        {
            bool b => b.ToString(),          // BaseLib 规范:bool → "True"/"False"
            string s => s,
            _ => value.ToString() ?? "",
        });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".new";
        using (FileStream stream = File.Create(tmp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            obj.WriteTo(writer);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
#nullable restore
