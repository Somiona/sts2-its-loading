using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 启动画面(boot.gd)自注入
//
// 帧 0 起效的 gd bootstrap:主题视觉全部在 mod 目录的 gd/json 文件里
// (仓库 src/ItsLoading/{render,themes} → 发行 mods/ItsLoading/{render,themes},
// render 发行只带 *.gd —— C# 编译进 dll,不随数据发行),C# 不内嵌 GDScript。
// Install 负责同步:
//   · render/ 与 themes/ 两棵树按字节差异刷新到 user://itsloading/
//     (引擎只认 res://、user:// 资源路径)
//   · override.cfg 的 autoload 指向 user://itsloading/render/boot.gd
//     (旧路径条目改写)
//   · 清掉旧版平铺布局(user://itsloading 根下的 boot.gd/<id>/)与单文件脚本
// 时序:帧 0 用上一次启动留下的拷贝,本次启动的 Init 再刷新;GdBridgeBar
// 晚期托管以 CACHE_MODE_IGNORE 读盘,拿到的就是刚刷新的新拷贝。
// 安装与锚点交接集中在本类;协议与主题装载见 render/boot.gd。

internal static class BootSplash
{
    private const string AutoloadName = "LoadingBarBoot";
    private const string GdUserDir = "user://itsloading";

    /// <summary>bootstrap 脚本的 user:// 路径(GdBridgeBar 晚期托管装载点)。</summary>
    internal const string BootGdUserPath = GdUserDir + "/render/boot.gd";

    private const string CfgMarker = "; LoadingBar mod autoload";

    /// <summary>旧版单文件方案的脚本路径,Install 时删除。</summary>
    private const string LegacyGdUserPath = "user://loadingbar_boot.gd";

    /// <summary>gd splash 自动载入节点名(GdBridgeBar 探测/Handoff 寻址共用)。</summary>
    internal static string AutoloadNodeName => AutoloadName;

    private static bool _injectedThisRun;

    /// <summary>本次运行是否刚完成注入(bootstrap 据此显示首次注入提示)。</summary>
    internal static bool InjectedThisRun => _injectedThisRun;

    internal static void Install()
    {
        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
        string cfgPath = Path.Combine(exeDir, "override.cfg");
        // mod 根下的两个镜像源:render/(gd 引导层,发行只带 *.gd)与 themes/(纯数据)
        string modRoot = Path.GetDirectoryName(typeof(ItsLoading).Assembly.Location) ?? ".";

        bool gdOk;
        try
        {
            // 非短路 &:三步都要跑完,再汇总结果
            gdOk = RefreshTree(Path.Combine(modRoot, "render"), "render")
                & RefreshTree(Path.Combine(modRoot, "themes"), "themes")
                & RemoveLegacyLayout();
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to refresh gd tree: {e}");
            gdOk = false;
        }

        try
        {
            string legacy = ProjectSettings.GlobalizePath(LegacyGdUserPath);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
                Log.Warn($"[ItsLoading] removed legacy boot script {LegacyGdUserPath}");
            }
        }
        catch { }

        bool cfgOk;
        try { cfgOk = EnsureCfgEntry(cfgPath); }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to update override.cfg: {e}");
            cfgOk = false;
        }

        if (cfgOk && gdOk) Log.Warn("[ItsLoading] boot splash already installed");
        _injectedThisRun = !cfgOk || !gdOk;
    }

    /// <summary>
    /// 树镜像同步(mod 根的 render/ 或 themes/ → user://itsloading/&lt;sub&gt;/):
    /// 字节一致跳过,不同覆盖,目标侧多余文件删除(源侧已改名/删除的主题文件
    /// 不在 user:// 残留)。返回"是否完全一致"(驱动 InjectedThisRun)。
    /// </summary>
    private static bool RefreshTree(string srcDir, string sub)
    {
        if (!Directory.Exists(srcDir))
        {
            Log.Error($"[ItsLoading] gd source dir missing: {srcDir}");
            return false;
        }
        string dstDir = Path.Combine(ProjectSettings.GlobalizePath(GdUserDir), sub);
        Directory.CreateDirectory(dstDir);
        bool allSame = true;

        foreach (string src in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, src);
            string dst = Path.Combine(dstDir, rel);
            bool same = File.Exists(dst)
                && System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(dst))
                    .SequenceEqual(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(src)));
            if (same) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            allSame = false;
            Log.Warn($"[ItsLoading] refreshed {GdUserDir}/{sub}/{rel.Replace('\\', '/')}");
        }

        if (Directory.Exists(dstDir))
        {
            foreach (string dst in Directory.GetFiles(dstDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dstDir, dst);
                if (File.Exists(Path.Combine(srcDir, rel))) continue;
                // 生成态文件不属镜像内容:主题包缓存(ThemePacks 每次启动重写,
                // gd 帧 0 读)不能被清扫删掉
                if (sub == "render" && rel == "theme-map.json") continue;
                File.Delete(dst);
                allSame = false;
                Log.Warn($"[ItsLoading] removed stale {GdUserDir}/{sub}/{rel.Replace('\\', '/')}");
            }
        }
        return allSame;
    }

    /// <summary>
    /// 清掉 v0.20 之前的平铺布局:user://itsloading 根下只允许 render/ 与
    /// themes/ 存在,旧版根级 boot.gd/kit.gd/&lt;主题id&gt;/ 整体删除
    /// (运行中的旧脚本已在内存,删文件无害)。有删除 = 不算"完全一致"。
    /// </summary>
    private static bool RemoveLegacyLayout()
    {
        string root = ProjectSettings.GlobalizePath(GdUserDir);
        if (!Directory.Exists(root)) return true;
        bool clean = true;
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(entry);
            if (name == "render" || name == "themes") continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
                clean = false;
                Log.Warn($"[ItsLoading] removed legacy layout entry {GdUserDir}/{name}");
            }
            catch (Exception e)
            {
                Log.Warn($"[ItsLoading] legacy cleanup skipped {name}: {e.Message}");
            }
        }
        return clean;
    }

    /// <summary>
    /// override.cfg:保证 [autoload] 段内有我们的条目(与标记注释成对)。
    /// 旧路径条目(user://loadingbar_boot.gd)改写为新路径;已有正确条目则不动。
    /// 段内可能共存其他 mod 的 autoload —— 我们的条目插进同一段,不另起段头。
    /// </summary>
    private static bool EnsureCfgEntry(string cfgPath)
    {
        string desired = $"{AutoloadName}=\"*{BootGdUserPath}\"";
        string body = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
        var lines = new List<string>(body.Split('\n').Select(l => l.TrimEnd('\r')));

        bool hasEntry = lines.Any(l => l.Trim() == desired);
        bool hasMarker = lines.Any(l => l.Trim().StartsWith(";") && l.Contains(CfgMarker));
        if (hasEntry && hasMarker) return true;

        // 去掉旧条目/旧标记,再插回当前条目
        lines = lines.Where(l =>
        {
            string t = l.Trim();
            bool ours = t.StartsWith(AutoloadName) && t.Contains('=');
            bool marker = t.StartsWith(";") && t.Contains(CfgMarker);
            return !ours && !marker;
        }).ToList();

        int insert = IndexOfSectionEnd(lines, "autoload");
        if (insert >= 0)
        {
            lines.Insert(insert, desired);
            lines.Insert(insert, CfgMarker);
        }
        else
        {
            lines.Add("");
            lines.Add(CfgMarker);
            lines.Add("");
            lines.Add("[autoload]");
            lines.Add("");
            lines.Add(desired);
        }

        File.WriteAllText(cfgPath, string.Join("\n", lines).TrimEnd('\n') + "\n");
        Log.Warn("[ItsLoading] wrote " + cfgPath);
        return false;
    }

    /// <summary>[section] 段尾索引(下一个段头之前;段到文件尾则行数)。无该段返回 -1。</summary>
    private static int IndexOfSectionEnd(List<string> lines, string section)
    {
        int header = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase))
            {
                header = i;
                break;
            }
        }
        if (header < 0) return -1;
        for (int i = header + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[")) return i;
        }
        return lines.Count;
    }

    internal static void Handoff()
    {
        var boot = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(AutoloadName);
        if (boot != null)
        {
            // 引擎启动锚点(gd frame 0)与前奏阶段 spans 写入启动时间线。
            // 晚期托管实例不叫这个名字 —— 锚点/工坊时序永远取 autoload 节点(帧 0 起算的那份)。
            Variant anchor = boot.Get("boot_start_msec");
            if (anchor.VariantType == Variant.Type.Int)
            {
                ItsLoading.Timeline?.SetBootAnchor(anchor.AsInt64());
            }
            // 工坊扫描时序 + 前奏活动行(gd 轮询观测)转成逐项 Prelude span,
            // 并灌入共用日志环(LoadingPresentation.SeedLog 前插,与时机无关);
            // 旧脚本没有 get_workshop_log,缺席则跳过。
            if (boot.HasMethod("get_workshop_log"))
            {
                ItsLoading.Run("read workshop timing", () =>
                {
                    Variant wl = boot.Call("get_workshop_log");
                    if (wl.VariantType != Variant.Type.Array) return;
                    var arr = wl.AsGodotArray();
                    if (arr.Count < 2) return;
                    double endMs = arr[0].AsDouble();
                    var names = arr.Count > 2 && arr[2].VariantType == Variant.Type.Dictionary
                        ? arr[2].AsGodotDictionary() : null;
                    if (arr.Count > 3 && arr[3].VariantType == Variant.Type.Array)
                    {
                        var prelude = new List<string>();
                        foreach (Variant line in arr[3].AsGodotArray())
                            prelude.Add(line.AsString());
                        if (prelude.Count > 0)
                        {
                            ItsLoading.Presentation?.SeedLog(prelude);
                            Log.Warn($"[ItsLoading] prelude log imported ({prelude.Count} lines)");
                        }
                    }
                    var entries = new List<(string, string, double)>();
                    if (arr[1].VariantType == Variant.Type.Array)
                    {
                        foreach (Variant e in arr[1].AsGodotArray())
                        {
                            var pair = e.AsGodotArray();
                            if (pair.Count < 2) continue;
                            string id = pair[0].AsString();
                            entries.Add((id,
                                names != null && names.ContainsKey(id) ? names[id].AsString() : "",
                                pair[1].AsDouble()));
                        }
                    }
                    if (entries.Count > 0)
                    {
                        ItsLoading.Timeline?.RecordWorkshopScan(entries, endMs);
                        Log.Warn($"[ItsLoading] workshop timing imported ({entries.Count} items)");
                    }
                });
            }
            Log.Warn(ItsLoading.Theme is GdBridgeBar
                ? "[ItsLoading] gd boot view retained as the active loading UI"
                : "[ItsLoading] autoload node present but not hosting (late host active)");
        }
        else
        {
            // 首次安装本次运行还未生效 / override.cfg 未被引擎采用 / gd 脚本加载失败。
            // 前两者由晚期托管兜底;日志里若同时没有任何 [LoadingBarBoot] 行,即加载失败。
            Log.Warn("[ItsLoading] no boot splash autoload node — late host is the UI this boot");
        }
    }
}
