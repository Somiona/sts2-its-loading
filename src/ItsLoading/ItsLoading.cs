using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

/// <summary>
/// v0.5 — 单一底部进度条,贯穿整个启动过程。
/// 设计原则(吃过的亏):
///   1. 不用 Container/CenterContainer —— 其布局走 deferred 排序,同步突发期间不执行(内容挤在 0×0)
///   2. 全部节点手动定位 —— v0.2 底部条验证过的唯一可靠模式
///   3. gd 与 C# 渲染完全一致的样式 —— frame 0 接管无视觉跳变
///   4. 单一进度刻度 0→1 由 BootTimeline 拥有:工坊读取 0-0.25 / mod 加载 0.25-0.60 / Essential 0.60-0.88 / 菜单就绪 0.88-1.0(启动边界 = 菜单可交互,延迟资产不进条)
///
/// 本类 = 启动编排层:Init 顺序、load-order 手术。UI 呈现在主题里(Themes/)。
/// 伴生模块:
///   BootTimeline.cs     —— 启动时间线(#3 深模块:刻度表 + span 记录 + 冻结;钩子经它推进度)
///   Patches/            —— Harmony 补丁族(#4:loader / boot phases / mod icon)
///   Themes/             —— 呈现缝(持久 gd 经典双条 + 首启 C# 经典双条兜底)
///   BootSplash.cs       —— gd splash 自注入/交接/延迟回收(帧 0→0.25 段的呈现)
///   WaterfallViewer.cs  —— 瀑布图查看器(菜单就绪后的调试 UI)
/// </summary>
[ModInitializer("Init")]
public static class ItsLoading
{
    /// <summary>本 mod 的清单 id(与 ItsLoading.json 一致;gd 侧经 @@MOD_ID@@ token 同源)。</summary>
    internal const string ModId = "ItsLoading";

    internal static readonly Stopwatch Sw = Stopwatch.StartNew();

    /// <summary>启动时间线(Init 最先创建;查询面 Api.LoadingDurations 与各补丁都经它)。</summary>
    internal static BootTimeline Timeline;

    /// <summary>当前主题(#7:Init 的 build bar 步骤创建;BaseLib 设置可循环切换,下次启动生效)。</summary>
    internal static ILoadingTheme Theme;

    public static void Init()
    {
        Log.Warn($"[ItsLoading] v{typeof(ItsLoading).Assembly.GetName().Version} initializer " +
                 $"@ +{Sw.ElapsedMilliseconds}ms frame={Engine.GetFramesDrawn()}");
        I18n.Init();
        int total = Math.Max(1, ModManager.Mods.Count);
        // 启动时间线:双时钟注入(构造即对表),呈现走推模型 —— Present 密度 = 加载活动密度
        // (Presenter 在 build bar 步骤接线到当前主题)
        Timeline = new BootTimeline(() => (long)Time.GetTicksMsec(), () => Sw.ElapsedTicks);
        Run("ensure boot splash installed", BootSplash.Install);
        int processed = 1;
        Run("ensure first in load order", () => processed = EnsureFirstInLoadOrder());
        string modStep = processed >= total
            ? I18n.T("bar.modsDone")
            : I18n.T("bar.mods", new() { ["n"] = processed.ToString(), ["t"] = total.ToString() });
        // 先建立时间线快照再建主题:gd 桥/首启 C# 兜底都从同一份初始状态绘制,
        // 不再短暂硬编码成 1/N。
        Timeline.BeginMods(total, processed, modStep);
        // 正常路径复用帧 0 gd 节点;首启/脚本版本不匹配才建 C# ClassicBar 兜底。
        Run("build bar", BuildTheme);
        Timeline.Replay();
        Run("boot splash handoff", BootSplash.Handoff);
        // 原子交接:此刻出帧 = 一帧内完成条与条的切换。
        // 连画 3 次:主线程刚进入同步突发时,首次提交可能被 MoltenVK 丢弃
        // (实测 mods 1-4 的单次 ForceDraw 不上屏、约 mod 5 才恢复),冗余提交
        // 让有效帧尽早出现(2026-08-27)。
        Run("first paint", () =>
        {
            for (int i = 0; i < 3; i++) RenderingServer.ForceDraw();
        });
        Run("patch loader", LoaderPatches.Install);
        Run("patch boot phases", BootPhasePatches.Install);
        Run("patch mod info icon", ModInfoIconPatches.Install);
        Log.Warn($"[ItsLoading] watching {total} mods ({processed} already processed)");
    }

    /// <summary>吞异常守卫:启动期任何一步失败只记日志,绝不中断游戏启动。</summary>
    internal static void Run(string what, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to {what}: {e}");
        }
    }

    // ================================================================ 主题接线

    /// <summary>
    /// 构建主题并接上时间线的呈现(Init 的 build bar 步骤)。
    /// 兼容的 gd 节点在场 → 作为唯一全程视图；否则构建同规格 ClassicBar 兜底。
    /// </summary>
    private static void BuildTheme()
    {
        Theme = GdBridgeBar.TryBuild() ?? ThemeRegistry.BuildActive();
        Timeline.Presenter = Theme.Present;
    }

    /// <summary>条移除(主题退休自身节点;gd splash takeover 归编排层)。</summary>
    internal static void RetireBar()
    {
        Theme?.Retire();
        BootSplash.Takeover(); // C# 条移除时才隐藏 gd splash
    }

    /// <summary>
    /// 耗时测量依赖"我们在其他 mod 之前加载"(补丁装上后才能观测后续加载)。
    /// 新安装/改名后 mod_list 没有我们 → 排序沉底,只能观测到尾部。
    /// 游戏 Initialize 结尾会按 _mods 顺序重建 mod_list,且优雅退出时由游戏
    /// 自行保存设置——因此只做内存重排,绝不自己写用户的 settings.save。
    /// (若首装后的第一次启动被强退,下次仍不完整,再下次自愈;可接受。)
    ///
    /// ⚠️ 时机陷阱(2026-08-26 todo#1):本方法运行在游戏 Initialize 的
    /// `foreach (Mod m in _mods) TryLoadMod(m)` 枚举体内(我们正是当前元素)。
    /// List&lt;T&gt; 枚举器每次 MoveNext 都校验 _version,RemoveAt/Insert 会让
    /// 下一次 MoveNext 抛 InvalidOperationException → 启动中止、mod_list 不重建,
    /// 首装玩家每次启动都崩。CallDeferred 也来不及(deferred 队列在同步突发期
    /// 不执行,而 mod_list 在同一方法末尾就重建了)。因此这里做"不触碰
    /// _version 的原地搬移":直接把 _items[0..idx-1] 右移一位、自身放到 [0],
    /// 不改 _size/_version——枚举器按 _items[_index] 现场取值,idx 之后的
    /// 未枚举元素原位不动,循环照常走完,Initialize 结尾照常按新顺序重建。
    /// </summary>
    private static int EnsureFirstInLoadOrder()
    {
        int loadedBeforeUs = ModManager.GetLoadedMods().Count();
        int idx = -1;
        for (int i = 0; i < ModManager.Mods.Count; i++)
        {
            if (ModManager.Mods[i].manifest?.id == ModId)
            {
                idx = i;
                break;
            }
        }
        int processed = idx >= 0 ? idx + 1 : Math.Max(1, loadedBeforeUs + 1);
        var mods = AccessTools.Field(typeof(ModManager), "_mods")?.GetValue(null)
            as System.Collections.Generic.List<Mod>;
        if (mods == null)
        {
            Log.Warn("[ItsLoading] _mods not accessible — load order left as-is");
            return processed;
        }
        if (idx < 0) return processed;
        if (idx == 0)
        {
            if (loadedBeforeUs > 0)
            {
                Log.Warn($"[ItsLoading] first in list but {loadedBeforeUs} mods loaded before us?");
            }
            return processed;
        }
        // 不用 RemoveAt+Insert(枚举中会崩),直接挪内部数组;内部结构不符合
        // 预期(未来 .NET 改字段)则放弃重排——优雅降级,绝不让游戏崩。
        var items = AccessTools.Field(typeof(System.Collections.Generic.List<Mod>), "_items")?
            .GetValue(mods) as Mod[];
        if (items == null || idx >= items.Length)
        {
            Log.Warn("[ItsLoading] List<T> internals not as expected — load order left as-is");
            return processed;
        }
        var me = items[idx];
        Array.Copy(items, 0, items, 1, idx);
        items[0] = me;
        Log.Warn($"[ItsLoading] moved self to load order #0 (was #{idx + 1}, " +
                 $"{loadedBeforeUs} mods loaded before us) — full timing coverage from next boot");
        return processed;
    }
}
