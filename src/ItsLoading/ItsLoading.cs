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
/// 本类 = 启动编排层:Init 顺序、进度条 UI(呈现)、load-order 手术。
/// 伴生模块:
///   BootTimeline.cs     —— 启动时间线(#3 深模块:刻度表 + span 记录 + 冻结;钩子经它推进度)
///   Patches/            —— Harmony 补丁族(#4:loader / boot phases / mod icon)
///   BootSplash.cs       —— gd splash 自注入/交接/延迟回收(帧 0→0.25 段的呈现)
///   WaterfallViewer.cs  —— 瀑布图查看器(菜单就绪后的调试 UI)
/// </summary>
[ModInitializer("Init")]
public static class ItsLoading
{
    internal static readonly Stopwatch Sw = Stopwatch.StartNew();

    /// <summary>启动时间线(Init 最先创建;查询面 Api.LoadingDurations 与各补丁都经它)。</summary>
    internal static BootTimeline Timeline;

    private static CanvasLayer _layer;
    private static Label _stepLabel;   // 第一行:当前步骤 + 计数
    private static Label _detailLabel; // 第二行:当前对象 + 耗时
    private static ColorRect _fill;    // 进度填充
    private static float _barFullWidth;

    private static bool UiOk => _layer != null;

    // 条移除后 AssetLoadingSession.Process 的 postfix 仍可能触发(游戏内房间加载等),
    // 用死亡标志防止对已释放节点的操作每帧抛异常
    private static bool _barDead;

    public static void Init()
    {
        Log.Warn($"[ItsLoading] v{typeof(ItsLoading).Assembly.GetName().Version} initializer " +
                 $"@ +{Sw.ElapsedMilliseconds}ms frame={Engine.GetFramesDrawn()}");
        I18n.Init();
        int total = Math.Max(1, ModManager.Mods.Count);
        // 启动时间线:双时钟注入(构造即对表),呈现走推模型 —— Present 密度 = 加载活动密度
        Timeline = new BootTimeline(() => (long)Time.GetTicksMsec(), () => Sw.ElapsedTicks)
        {
            Presenter = SetProgress,
        };
        Run("ensure boot splash installed", BootSplash.Install);
        Run("ensure first in load order", EnsureFirstInLoadOrder);
        // 顺序关键:先建好条并强制绘制一帧,再隐藏 autoload splash,保证无黑屏间隙
        Run("build bar", BuildBar);
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
        Log.Warn($"[ItsLoading] watching {total} mods");
        // todo#9:走 I18n(此前硬编码中文,英文玩家从首帧 ForceDraw 起就看中文;
        // total==1 时中文停留整条进度条生命周期)
        Timeline.BeginMods(total,
            I18n.T("bar.mods", new() { ["n"] = "1", ["t"] = total.ToString() }));
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

    // ================================================================ UI(全手动定位,无 Container)

    // ---- 条样式共享常量(todo#10:gd splash 模板与 C# 条的唯一真源)----
    // 颜色是最易漂移的样式类(几何漂移肉眼立见,颜色微漂正是一次漏网),
    // 因此颜色由这里的常量插值进 BootSplash.cs 的模板;几何常量仍两侧成对,
    // 改布局时需手动同步(见模板内注释)。
    internal static readonly Color BarTrackColor = new(1f, 1f, 1f, 0.15f);        // 轨道
    internal static readonly Color BarDetailColor = new(0.62f, 0.64f, 0.70f, 1f); // 细节文字
    internal static readonly Color BarFillColor = new(0.2f, 0.85f, 0.9f, 1f);     // 填充

    private static void BuildBar()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 vs = tree.Root.GetVisibleRect().Size;

        _layer = new CanvasLayer { Layer = 999 };

        // 定位容器 + 不透明黑垫底(上下各溢出 2px 盖严):
        // v0.13.x 双条并存期间,gd 条(998,冻结在「52/52」)一直在本条(999)下面渲染,
        // 两条都是透明设计时文字会交叠(用户可见的"gd 残留")。C# 条一旦开始出帧,
        // 同几何黑底即完全遮住 gd 条——交接时刻无论早晚都无缝(2026-08-27)。
        var strip = new Control();
        strip.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        strip.OffsetTop = -64f;
        _layer.AddChild(strip);
        var backing = new ColorRect { Color = new Color(0f, 0f, 0f, 1f) };
        backing.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backing.OffsetTop = -2f;
        backing.OffsetBottom = 2f;
        strip.AddChild(backing);

        _stepLabel = new Label();
        _stepLabel.Position = new Vector2(24f, 8f);
        _stepLabel.AddThemeFontSizeOverride("font_size", 20);
        _stepLabel.AddThemeColorOverride("font_color", Colors.White);
        _stepLabel.Text = I18n.T("bar.mods", new() { ["n"] = "1", ["t"] = Math.Max(1, ModManager.Mods.Count).ToString() });
        strip.AddChild(_stepLabel);

        _detailLabel = new Label();
        _detailLabel.Position = new Vector2(24f, 36f);
        _detailLabel.AddThemeFontSizeOverride("font_size", 14);
        _detailLabel.AddThemeColorOverride("font_color", BarDetailColor);
        _detailLabel.Text = "";
        strip.AddChild(_detailLabel);

        _barFullWidth = vs.X - 48f;
        var track = new ColorRect();
        track.Position = new Vector2(24f, 56f);
        track.Size = new Vector2(_barFullWidth, 5f);
        track.Color = BarTrackColor;
        strip.AddChild(track);

        _fill = new ColorRect();
        _fill.Position = Vector2.Zero;
        _fill.Size = new Vector2(0f, 5f);
        _fill.Color = BarFillColor;
        track.AddChild(_fill);

        // 首次注入提示(左上角,不挡条)
        if (BootSplash.InjectedThisRun)
        {
            var hint = new Label();
            hint.Text = I18n.T("hint.injected");
            hint.Position = new Vector2(24f, 24f);
            hint.AddThemeFontSizeOverride("font_size", 14);
            hint.AddThemeColorOverride("font_color", new Color(0.2f, 0.85f, 0.9f, 1f));
            _layer.AddChild(hint);
            var t = tree.CreateTimer(8.0);
            t.Timeout += () => Run("hide injection hint", () => { if (GodotObject.IsInstanceValid(hint)) hint.QueueFree(); });
        }

        // 必须直接 AddChild:同步突发期间 deferred 队列永远不会执行
        // 注意:这里不 ForceDraw —— 画帧时机在隐藏 gd splash 之后,保证交接原子性
        tree.Root.AddChild(_layer);
        Log.Warn($"[ItsLoading] bar attached (viewport {vs.X}x{vs.Y})");
    }

    /// <summary>唯一的进度呈现入口(BootTimeline.Presenter 的目标):条宽 + 两行文案 + 强制出帧。</summary>
    private static void SetProgress(float frac, string step, string detail, bool forceDraw = true)
    {
        if (!UiOk || _barDead) return;
        try
        {
            _fill.Size = new Vector2(_barFullWidth * Math.Clamp(frac, 0f, 1f), 5f);
            if (step != null) _stepLabel.Text = step;
            if (detail != null) _detailLabel.Text = detail;
            if (forceDraw) RenderingServer.ForceDraw();
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to update bar: {e}");
        }
    }

    /// <summary>条移除(C# 条 QueueFree + gd splash takeover;置 _barDead 挡住移除后的 postfix)。</summary>
    internal static void RetireBar()
    {
        _barDead = true;
        _layer?.QueueFree();
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
    private static void EnsureFirstInLoadOrder()
    {
        int loadedBeforeUs = ModManager.GetLoadedMods().Count();
        var mods = AccessTools.Field(typeof(ModManager), "_mods").GetValue(null)
            as System.Collections.Generic.List<Mod>;
        if (mods == null)
        {
            Log.Warn("[ItsLoading] _mods not accessible — load order left as-is");
            return;
        }
        int idx = mods.FindIndex(m => m.manifest?.id == "ItsLoading");
        if (idx < 0) return;
        if (idx == 0)
        {
            if (loadedBeforeUs > 1)
            {
                Log.Warn($"[ItsLoading] first in list but {loadedBeforeUs - 1} mods loaded before us?");
            }
            return;
        }
        // 不用 RemoveAt+Insert(枚举中会崩),直接挪内部数组;内部结构不符合
        // 预期(未来 .NET 改字段)则放弃重排——优雅降级,绝不让游戏崩。
        var items = AccessTools.Field(typeof(System.Collections.Generic.List<Mod>), "_items")
            .GetValue(mods) as Mod[];
        if (items == null || idx >= items.Length)
        {
            Log.Warn("[ItsLoading] List<T> internals not as expected — load order left as-is");
            return;
        }
        var me = items[idx];
        Array.Copy(items, 0, items, 1, idx);
        items[0] = me;
        Log.Warn($"[ItsLoading] moved self to load order #0 (was #{idx + 1}, " +
                 $"{loadedBeforeUs} mods loaded before us) — full timing coverage from next boot");
    }
}
