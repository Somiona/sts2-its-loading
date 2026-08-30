using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

/// <summary>
/// 单一底部进度条,贯穿整个启动过程。本类是启动编排层:Init 顺序与
/// load-order 调整;进度刻度在 BootTimeline,UI 呈现在 Themes/。
///
/// 布局约束(同步突发期间 deferred 不执行):
///   1. 不用 Container/CenterContainer,全部节点手动定位
///   2. 0→1 全程刻度由 BootTimeline 拥有(刻度表见该文件)
///
/// 模块导览:
///   BootTimeline.cs     —— 启动时间线(刻度表 + span 记录 + 冻结;钩子经它推进度)
///   Patches/            —— Harmony 补丁(loader / boot phases / mod icon)
///   Themes/             —— 主题呈现(C# 接口 + gd 文件:boot.gd / kit.gd / &lt;id&gt;/theme.gd)
///   BootSplash.cs       —— gd 树自注入(user://itsloading 差异刷新)与锚点交接
///   WaterfallViewer.cs  —— 瀑布图查看器(菜单就绪后的调试 UI)
/// </summary>
[ModInitializer("Init")]
public static class ItsLoading
{
    /// <summary>本 mod 的 manifest id,须与 ItsLoading.json、boot.gd 的 MOD_ID 一致。</summary>
    internal const string ModId = "ItsLoading";

    internal static readonly Stopwatch Sw = Stopwatch.StartNew();

    /// <summary>启动时间线,Init 最先创建;进度上报与 Api.LoadingDurations 的查询都走它。</summary>
    internal static BootTimeline Timeline;

    /// <summary>当前主题,Init 的 build bar 步骤创建;在 BaseLib 设置里切换,下次启动生效。</summary>
    internal static ILoadingTheme Theme;

    public static void Init()
    {
        Log.Warn($"[ItsLoading] v{typeof(ItsLoading).Assembly.GetName().Version} initializer " +
                 $"@ +{Sw.ElapsedMilliseconds}ms frame={Engine.GetFramesDrawn()}");
        I18n.Init();
        int total = Math.Max(1, ModManager.Mods.Count);
        // 双时钟注入,构造时记下两者的偏移;呈现是 push 模型 —— Present 密度
        // = 加载活动密度(Presenter 在 build bar 步骤接到主题)
        Timeline = new BootTimeline(() => (long)Time.GetTicksMsec(), () => Sw.ElapsedTicks);
        Run("ensure boot splash installed", BootSplash.Install);
        // 主题 cfg 迁移/补默认(必须在 BuildTheme 前:先于 BaseLib 加载,
        // 它的 Load() 才能读到完整 Theme 键,不触发缺键重存)
        Run("migrate theme cfg", ThemeRegistry.MigrateToCfg);
        int processed = 1;
        Run("ensure first in load order", () => processed = EnsureFirstInLoadOrder());
        string modStep = processed >= total
            ? I18n.T("bar.modsDone")
            : I18n.T("bar.mods", new() { ["n"] = processed.ToString(), ["t"] = total.ToString() });
        // 先建立时间线快照再建主题,主题从同一份初始状态起画,
        // 不会短暂显示硬编码的 1/N。
        Timeline.BeginMods(total, processed, modStep);
        // 正常路径复用帧 0 的 gd 节点;首装/脚本版本不匹配走晚期托管(见 GdBridgeBar)。
        Run("build bar", BuildTheme);
        Timeline.Replay();
        Run("boot splash handoff", BootSplash.Handoff);
        // 此刻出帧即完成旧条到新条的切换。连画 3 次:同步突发刚开始时首次
        // 提交可能被 MoltenVK 丢弃(mods 1-4 的单次 ForceDraw 不上屏,约
        // mod 5 恢复),冗余提交让有效帧尽早出现。
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
    /// 构建主题并把时间线的呈现接到它上面(Init 的 build bar 步骤)。
    /// 只走 gd boot 视图:autoload 桥接,或晚期托管(首装 / 版本过渡 /
    /// 旧视图已下线)。再失败 = 本次启动没有加载 UI —— 晚期托管已覆盖上述
    /// 路径,仍失败说明 gd 文件本身有问题(重装 mod 可修复)。
    /// </summary>
    private static void BuildTheme()
    {
        Theme = GdBridgeBar.TryBuild();
        if (Theme != null) Timeline.Presenter = Theme.Present;
        else Log.Error("[ItsLoading] no loading UI this boot (see gd host logs above)");
    }

    /// <summary>移除进度条(由主题自己完成 —— GdBridgeBar 会对其宿主节点调 takeover)。</summary>
    internal static void RetireBar() => Theme?.Retire();

    /// <summary>
    /// 耗时测量依赖"我们在其他 mod 之前加载"(补丁装上后才能观测后续加载)。
    /// 新安装/改名后 mod_list 没有我们 → 排序沉底,只能观测到尾部。
    /// 游戏 Initialize 结尾会按 _mods 顺序重建 mod_list,退出时由游戏自行
    /// 保存设置,因此这里只做内存重排,绝不写用户的 settings.save。
    /// (首装后的第一次启动若被强退,下次仍不完整,再下次自动恢复;可接受。)
    ///
    /// ⚠️ 时机陷阱:本方法运行在游戏 Initialize 的
    /// `foreach (Mod m in _mods) TryLoadMod(m)` 枚举体内(我们正是当前元素)。
    /// List&lt;T&gt; 枚举器每次 MoveNext 都校验 _version,RemoveAt/Insert 会让
    /// 下一次 MoveNext 抛 InvalidOperationException → 启动中止、mod_list
    /// 不重建,首装玩家每次启动都崩。CallDeferred 也来不及(deferred 队列
    /// 在同步突发期不执行,而 mod_list 在同一方法末尾就重建了)。因此这里做
    /// 不触碰 _version 的原地搬移:直接把 _items[0..idx-1] 右移一位、自身
    /// 放到 [0],不改 _size/_version —— 枚举器按 _items[_index] 现场取值,
    /// idx 之后的未枚举元素原位不动,循环照常走完,Initialize 结尾照常按
    /// 新顺序重建。
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
        // 不用 RemoveAt+Insert(枚举中会崩),直接挪内部数组;.NET 改了内部
        // 字段名拿不到就放弃重排,不影响启动。
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
