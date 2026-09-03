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
/// load-order 调整;进度刻度在 BootTimeline,UI 呈现在 render/ 与 themes/。
///
/// 布局约束(同步突发期间 deferred 不执行):
///   1. 不用 Container/CenterContainer,全部节点手动定位
///   2. 0→1 全程刻度由 BootTimeline 拥有(刻度表见该文件)
///
/// 模块导览:
///   BootTimeline.cs     —— 启动时间线(刻度表 + span 记录 + 冻结;钩子经它推进度)
///   Patches/            —— Harmony 补丁(loader / boot phases / mod icon)
///   render/             —— 加载屏呈现层(gd 引导 boot.gd/kit.gd/interpreter.gd +
///                          C# 桥 GdBridgeBar + 视图模型 LoadingPresentation +
///                          主题包发现 ThemePacks + 平台原生呈现面 macos/、windows/)
///   themes/             —— 主题数据(themes/&lt;id&gt;/theme.json + 素材;内置主题,
///                          外部包经 ThemePacks 发现)
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

    /// <summary>帧 0 起存在的 Godot 基础呈现 adapter。</summary>
    internal static IGodotSurface GodotSurface;

    /// <summary>Godot 基础路径与可选 native 接管路径的唯一所有者。</summary>
    internal static SurfaceRouter Router;

    /// <summary>唯一加载屏视图模型(文案包装/日志环/时间采样):Presenter 组合点
    /// 先经它,再并联喂 gd 主题与原生呈现面 —— 见 render/LoadingPresentation.cs。</summary>
    internal static LoadingPresentation Presentation;

    public static void Init()
    {
        Log.Warn($"[ItsLoading] v{typeof(ItsLoading).Assembly.GetName().Version} initializer " +
                 $"@ +{Sw.ElapsedMilliseconds}ms frame={Engine.GetFramesDrawn()}");
        Run("freeze probe init", FreezeProbe.Init);
        FreezeProbe.Sample("init");
        I18n.Init();
        // 唯一视图模型先于一切呈现器建立(阶段文本包装与 gd 旧 _stage_text 同式同键)
        Presentation = new LoadingPresentation((stage, step) => I18n.T("bar.stage", new()
        {
            ["n"] = stage.ToString(),
            ["t"] = LoadingViewState.StageCount.ToString(),
            ["name"] = step,
        }));
        int total = Math.Max(1, ModManager.Mods.Count);
        // 双时钟注入,构造时记下两者的偏移;呈现是 push 模型 —— Present 密度
        // = 加载活动密度(Presenter 在 build bar 步骤接到主题)
        Timeline = new BootTimeline(() => (long)Time.GetTicksMsec(), () => Sw.ElapsedTicks);
        // 主题发现属于共享基础设施，不能受可选 native 路径开关影响。
        string builtinThemes = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(ItsLoading).Assembly.Location) ?? ".",
            "themes");
        Run("discover themes", () => ThemePacks.DiscoverAndCache(builtinThemes));

        Func<IThemeSurface> nativeFactory = null;
        bool allowNative = ThemeRegistry.NativeRendererEnabled();
        if (allowNative && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()))
        {
            // 原生呈现面只消费 ThemeCompiler 产出的确定 ThemePlan；
            // 加载失败由面内细条兜底,不阻断
            ThemePlan nativePlan = null;
            string nativeThemeDir = "";
            Run("compile native theme plan", () =>
            {
                // 发现层先于消费:Init 早期,ModManager 已填全 _mods(先于任何 TryLoadMod)
                string id = ThemeRegistry.Current();
                nativeThemeDir = ThemePacks.DirOf(id) ?? System.IO.Path.Combine(builtinThemes, id);
                nativePlan = ThemeCompiler.Compile(nativeThemeDir, w => Log.Warn(w));
                Log.Warn(nativePlan != null
                    ? $"[ItsLoading] native theme plan compiled ({id}, {nativePlan.Elements.Count} elements)"
                    : "[ItsLoading] native theme plan unavailable — thin-bar fallback");
            });
            string version = typeof(ItsLoading).Assembly.GetName().Version?.ToString() ?? "";
            if (nativePlan == null || nativePlan.SupportsNative)
            {
                nativeFactory = OperatingSystem.IsMacOS()
                    ? () => new MacLayerSurface(nativePlan, nativeThemeDir, version,
                        k => I18n.T(k), ThemeRegistry.CalibViewEnabled())
                    : () => new WindowsLayerSurface(nativePlan, nativeThemeDir, version,
                        k => I18n.T(k), ThemeRegistry.CalibViewEnabled());
            }
            else
            {
                Log.Warn("[ItsLoading] selected theme keeps Godot baseline; native incompatibilities: "
                    + string.Join("; ", nativePlan.NativeIncompatibilities));
            }
        }
        else if (!allowNative)
        {
            Log.Warn("[ItsLoading] native renderer OFF (setting) — Godot baseline only this boot");
        }
        else
        {
            Log.Warn("[ItsLoading] native renderer unavailable on this platform — Godot baseline only");
        }
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
        Run("build bar", () => BuildTheme(nativeFactory));
        // 前奏所有权先一次性交给 C# presentation，再从唯一快照出口 replay。
        Run("boot splash handoff", BootSplash.Handoff);
        Timeline.Replay();
        // 此刻出帧即完成旧条到新条的切换。连画 3 次:同步突发刚开始时首次
        // 提交可能被 MoltenVK 丢弃(mods 1-4 的单次 ForceDraw 不上屏,约
        // mod 5 恢复),冗余提交让有效帧尽早出现。
        Run("first paint", () =>
        {
            for (int i = 0; i < 3; i++) RenderingServer.ForceDraw();
        });
        FreezeProbe.Sample("after-first-paint");
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
    private static void BuildTheme(Func<IThemeSurface> nativeFactory)
    {
        GodotSurface = GdBridgeBar.TryBuild();
        if (GodotSurface == null)
            Log.Error("[ItsLoading] no loading UI this boot (see gd host logs above)");
        Router = new SurfaceRouter(
            GodotSurface,
            () => Engine.GetFramesDrawn(),
            () => System.Threading.Thread.CurrentThread.ManagedThreadId == 1,
            System.Environment.GetEnvironmentVariable,
            nativeFactory,
            s => Log.Warn(s));
        Timeline.Connect(s => Router.Present(Presentation.Present(s)));
    }

    /// <summary>
    /// 移除进度条：退场细节全部封装在 SurfaceRouter；编排层只发一次 retire。
    /// </summary>
    internal static async void RetireBar()
    {
        if (Router == null) return;
        try
        {
            await Router.Retire("retire");
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading] loading surface retire failed: {e.Message}");
        }
    }

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
