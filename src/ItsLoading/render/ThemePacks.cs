using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MegaCrit.Sts2.Core.Logging;

#nullable enable

namespace ItsLoading;

/// <summary>
/// 主题包发现层(Inc 8):主题 id → 主题目录(绝对路径)的唯一裁决者。
/// 两个来源:
///   · 内置 —— 本 mod 的 themes/&lt;id&gt;/(随 mod 发行)
///   · 外部包 —— 任何普通 mod 携带 themes/&lt;id&gt;/theme.json(数据-only mod,
///     工坊发布即主题包;封闭词汇表 = 无代码执行,见 ThemeDef/interpreter)
/// 发现时机:我们的 Init(ModManager.Initialize 已填好全量 _mods 列表,
/// 先于任何 TryLoadMod)→ 内存注册表 + 重写缓存文件(供 gd 下次启动帧 0 读)。
/// gd 帧 0 解析链:镜像 themes/&lt;id&gt; → 缓存 → classic(见 boot.gd);
/// 缓存一次性滞后是设计内行为(安装包后的第一次启动 phase 0 显示 classic,
/// 自我们 Init 起全部阶段正确)。纯扫描/合并逻辑注入 IO,离线可单测。
/// </summary>
internal static class ThemePacks
{
    /// <summary>缓存文件(gd 帧 0 读;放 render/ 下 —— 根级会被布局清扫删除)。</summary>
    internal const string CachePath = "user://itsloading/render/theme-map.json";

    /// <summary>一个可用主题:id + 主题目录(= &lt;themes 根&gt;/&lt;id&gt;)+ 来源 mod id。</summary>
    internal readonly record struct ThemeEntry(string Id, string Dir, string ModId);

    // ---- 纯逻辑(测试注入文件系统抽象) ----

    /// <summary>
    /// 合并内置与包目录:id 冲突内置赢(包不可遮蔽内置),包之间先到先得
    /// (ModManager 列表序);非法 id / 无 theme.json 的目录跳过。
    /// </summary>
    internal static List<ThemeEntry> Merge(
        IEnumerable<(string modId, string themesDir)> sources,
        Action<string>? warn = null)
    {
        var byId = new Dictionary<string, ThemeEntry>();
        foreach (var (modId, themesDir) in sources)
        {
            foreach (ThemeEntry e in ScanThemes(modId, themesDir))
            {
                if (byId.ContainsKey(e.Id))
                {
                    if (byId[e.Id].ModId != e.ModId)
                        warn?.Invoke($"[ItsLoading] theme id '{e.Id}' from {e.ModId} ignored " +
                                     $"(already provided by {byId[e.Id].ModId})");
                    continue;
                }
                byId[e.Id] = e;
            }
        }
        return byId.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<ThemeEntry> ScanThemes(string modId, string themesDir)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(themesDir);
            if (!dir.Exists) yield break;
        }
        catch
        {
            yield break;
        }
        foreach (var sub in dir.EnumerateDirectories())
        {
            string id = sub.Name.ToLowerInvariant();
            if (!ThemeRegistry.IsValidId(id)) continue;
            if (!File.Exists(Path.Combine(sub.FullName, "theme.json"))) continue;
            yield return new ThemeEntry(id, sub.FullName, modId);
        }
    }

    /// <summary>缓存 JSON(id → 目录);坏目录读时由 gd 兜底,写时不再过滤。</summary>
    internal static string SerializeCache(List<ThemeEntry> themes)
    {
        var map = new System.Text.StringBuilder("{\n");
        for (int i = 0; i < themes.Count; i++)
        {
            map.Append("  \"").Append(themes[i].Id).Append("\": \"")
               .Append(themes[i].Dir.Replace("\\", "\\\\").Replace("\"", "\\\""))
               .Append("\"").Append(i == themes.Count - 1 ? "\n" : ",\n");
        }
        map.Append("}\n");
        return map.ToString();
    }

    /// <summary>解析缓存(逐条剔除已失效目录);任何损坏 → 空表(帧 0 回 classic)。</summary>
    internal static List<ThemeEntry> ParseCache(string? json, Func<string, bool> dirHasThemeJson)
    {
        var outList = new List<ThemeEntry>();
        if (json == null) return outList;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return outList;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                string id = prop.Name.ToLowerInvariant();
                string dir = prop.Value.GetString() ?? "";
                if (!ThemeRegistry.IsValidId(id) || !dir.StartsWith("/")
                    || !dirHasThemeJson(Path.Combine(dir, "theme.json"))) continue;
                outList.Add(new ThemeEntry(id, dir, "pack"));
            }
        }
        catch (System.Text.Json.JsonException) { }
        return outList;
    }

    // ---- 运行时(游戏进程内) ----

    private static List<ThemeEntry>? _discovered;

    /// <summary>本 boot 的注册表(Init 里 DiscoverAndCache 之后可用;之前为空)。</summary>
    internal static IReadOnlyList<ThemeEntry> Discovered => _discovered ?? new List<ThemeEntry>();

    /// <summary>按 id 取目录(内置与包统一);未发现 → null(调用方走 classic)。</summary>
    internal static string? DirOf(string id) =>
        _discovered?.FirstOrDefault(e => e.Id == id) is { } e ? e.Dir : null;

    /// <summary>
    /// Init 步骤(先于 BuildTheme):扫内置 + 全部已发现 mod 的 themes/,
    /// 过滤被禁用的 mod,写缓存。ModManager.Mods 此刻已填全(TryLoadMod 未开始,
    /// state 全 None,启用态经 settings 反射查询 —— TryLoadMod 同款判定)。
    /// </summary>
    internal static void DiscoverAndCache(string builtinThemesDir)
    {
        try
        {
            var sources = new List<(string, string)> { (ItsLoading.ModId, builtinThemesDir) };
            foreach (var mod in MegaCrit.Sts2.Core.Modding.ModManager.Mods)
            {
                string? modId = mod.manifest?.id;
                string? path = mod.path;
                if (modId == null || path == null || modId == ItsLoading.ModId) continue;
                if (IsModDisabled(modId, mod)) continue;
                sources.Add((modId, Path.Combine(path, "themes")));
            }
            _discovered = Merge(sources, w => Log.Warn(w));
            string cache = SerializeCache(_discovered.Where(e => e.ModId != ItsLoading.ModId).ToList());
            string cachePath = Godot.ProjectSettings.GlobalizePath(CachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, cache);
            Log.Warn($"[ItsLoading] themes: {_discovered.Count} total " +
                     $"({_discovered.Count(e => e.ModId != ItsLoading.ModId)} from packs)");
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to discover theme packs: {e}");
            _discovered = null; // 内置路径由调用方镜像兜底
        }
    }

    /// <summary>启用态判定(复刻 TryLoadMod 的 _settings.IsModDisabled;反射缺席 = 启用)。</summary>
    private static bool IsModDisabled(string modId, MegaCrit.Sts2.Core.Modding.Mod mod)
    {
        try
        {
            var settings = HarmonyLib.AccessTools.Field(
                typeof(MegaCrit.Sts2.Core.Modding.ModManager), "_settings")?.GetValue(null);
            if (settings == null) return false;
            var method = HarmonyLib.AccessTools.Method(settings.GetType(), "IsModDisabled");
            if (method == null) return false;
            return method.Invoke(settings, new object[] { modId, mod.modSource }) is true;
        }
        catch
        {
            return false;
        }
    }
}
