using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
///   4. 单一进度刻度 0→1:工坊读取 0-0.25 / mod 加载 0.25-0.60 / Essential 0.60-0.88 / 菜单就绪 0.88-1.0(启动边界 = 菜单可交互,延迟资产不进条)
///
/// 伴生模块(架构拆分后从本文件移出):
///   BootSplash.cs      —— gd splash 自注入/交接/延迟回收(帧 0→0.25 段的呈现)
///   WaterfallViewer.cs —— 瀑布图查看器(菜单就绪后的调试 UI)
/// </summary>
[ModInitializer("Init")]
public static class ItsLoading
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static long _lastMs;
    private static int _count = 1; // 本 mod 自己算第 1 个
    private static int _total = 1;
    private static bool _done;
    private static bool _menuHandled;

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
        _total = Math.Max(1, ModManager.Mods.Count);
        // 时钟对表:把 C# Stopwatch 时间轴换算到引擎时间轴(gd 第 0 帧起算)
        Recorder.EngineOffsetMs = (long)Time.GetTicksMsec() - Sw.Elapsed.TotalMilliseconds;
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
        Run("patch loader", PatchLoader);
        Run("patch boot phases", PatchBootPhases);
        Run("patch mod info icon", PatchModInfoIcon);
        Log.Warn($"[ItsLoading] watching {_total} mods");
        // todo#9:走 I18n(此前硬编码中文,英文玩家从首帧 ForceDraw 起就看中文;
        // _total==1 时中文停留整条进度条生命周期)
        SetProgress(0.25f,
            I18n.T("bar.mods", new() { ["n"] = "1", ["t"] = _total.ToString() }), "", true);
    }

    // ---------------------------------------------------------------- mod 菜单图标
    //
    // 游戏读图标走 ResourceLoader("res://<id>/mod_image.png"),而导出版 Godot
    // 无法加载未导入的裸 PNG(需要 BaseLib 那种 .godot/imported/*.ctex + remap 链)。
    // 所以改为 patch mod 信息面板:从 mod 目录磁盘直接读图,运行时 Image API 认裸 PNG。

    private static void PatchModInfoIcon()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.icon");
        var fill = AccessTools.Method(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer"),
            "Fill");
        if (fill == null)
        {
            Log.Warn("[ItsLoading] NModInfoContainer.Fill not found — icon patch skipped");
            return;
        }
        harmony.Patch(fill, postfix: new HarmonyMethod(typeof(ItsLoading), nameof(AfterModInfoFill)));
        Log.Warn("[ItsLoading] mod info icon patch installed");
    }

    private static void AfterModInfoFill(object __instance, Mod mod)
    {
        if (mod?.manifest?.id != "ItsLoading") return;
        Run("set mod icon", () =>
        {
            string imgPath = Path.Combine(mod.path, "mod_image.png");
            if (!File.Exists(imgPath))
            {
                Log.Warn("[ItsLoading] mod_image.png not found next to dll: " + imgPath);
                return;
            }
            var image = Image.LoadFromFile(imgPath);
            var tex = ImageTexture.CreateFromImage(image);
            var rect = (TextureRect)AccessTools.Field(
                AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer"),
                "_image").GetValue(__instance);
            if (rect != null && tex != null)
            {
                rect.Texture = tex;
                Log.Warn("[ItsLoading] mod menu icon set from " + imgPath);
            }
        });
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
        _stepLabel.Text = I18n.T("bar.mods", new() { ["n"] = "1", ["t"] = _total.ToString() });
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

    /// <summary>唯一的进度更新入口:条宽 + 两行文案 + 强制出帧。</summary>
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

    // ================================================================ Harmony patches

    private static void PatchLoader()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading");
        var original = AccessTools.Method(typeof(ModManager), "TryLoadMod");
        if (original == null)
        {
            Log.Error("[ItsLoading] TryLoadMod not found — game update changed signature?");
            return;
        }
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeTryLoadMod)),
            postfix: new HarmonyMethod(typeof(ItsLoading), nameof(AfterModLoad)));
        Log.Warn("[ItsLoading] TryLoadMod prefix+postfix installed OK");
    }

    private static void BeforeTryLoadMod(Mod mod)
    {
        // 单次时钟读;mod 段总区间起点
        Recorder.PrefixCalls++;
        Recorder.ModStartTicks = Sw.ElapsedTicks;
        if (Recorder.FirstModTicks < 0) Recorder.FirstModTicks = Recorder.ModStartTicks;
        // 突发期前缀补画:AfterModLoad 之外在每个 mod 开始时多一次提交,
        // 压缩"首次有效出帧"前的盲区(同 first paint 的冗余提交理由)
        if (UiOk && !_barDead) RenderingServer.ForceDraw();
    }

    private static void AfterModLoad(Mod mod)
    {
        long now = Sw.ElapsedMilliseconds;
        long delta = now - _lastMs;
        _lastMs = now;
        _count++;
        string id = mod.manifest?.id ?? "<null>";

        Log.Warn($"[ItsLoading] [{_count}/{_total}] {id} -> {mod.state} " +
                 $"+{delta}ms frame={Engine.GetFramesDrawn()}");

        // 耗时记录(prefix→postfix 真实区间,亚毫秒精度)
        long nowTicks = Sw.ElapsedTicks;
        Recorder.LastModTicks = nowTicks;
        Recorder.ModSpans.Add(new Api.LoadSpan(
            id, Api.LoadPhase.ModLoad,
            Recorder.ToEngineMs(Recorder.ModStartTicks),
            (nowTicks - Recorder.ModStartTicks) * Recorder.SwTicksToMs,
            mod.state.ToString()));

        // BaseLib 加载完成的瞬间注册瀑布图入口(软依赖:编译期引用 + JIT 方法级隔离,
        // BaseLib 缺席时 RegisterInBaseLib 永不被调用、类型永不加载)
        if (id == "BaseLib" && mod.state == ModLoadState.Loaded)
        {
            Run("register in BaseLib", WaterfallViewer.RegisterInBaseLib);
        }

        float frac = 0.25f + 0.35f * (_count / (float)_total);
        SetProgress(frac, I18n.T("bar.mods", new() { ["n"] = _count.ToString(), ["t"] = _total.ToString() }), $"{id} · +{delta}ms");

        if (_count >= _total && !_done)
        {
            _done = true;
            Recorder.PhaseSpans.Add(new Api.LoadSpan(
                "phase.mod_load", Api.LoadPhase.ModLoad,
                Recorder.ToEngineMs(Recorder.FirstModTicks),
                (Recorder.LastModTicks - Recorder.FirstModTicks) * Recorder.SwTicksToMs,
                $"{_count} mods"));
            SetProgress(0.60f, I18n.T("bar.modsDone"), $"{_count} · {Sw.ElapsedMilliseconds}ms");
            Log.Warn($"[ItsLoading] all mods processed @ +{Sw.ElapsedMilliseconds}ms");
        }
    }

    // ---- 启动子步骤(Essential 同步长黑屏期间的 checkpoints) ----

    private static readonly (string Type, string Method, string Label, float Progress)[] Steps =
    {
        ("MegaCrit.Sts2.Core.Assets.AtlasManager", "LoadEssentialAtlases", "step.atlas", 0.615f),
        ("MegaCrit.Sts2.Core.Localization.LocManager", "Initialize", "step.loc", 0.625f),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Init", "step.modeldb", 0.635f),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "InitIds", "step.ids", 0.645f),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Preload", "step.preload", 0.655f),
    };

    private static readonly System.Collections.Generic.Dictionary<MethodBase, (string Label, float Progress)> StepMap =
        new();

    // ---- 资产会话实时进度(NAssetLoader._Process 主线程每帧泵 AssetLoadingSession.Process) ----

    /// <summary>
    /// 启动期的会话名 → 单条进度刻度上的子区间。游戏内会话(房间/角色)不在此表,自动忽略。
    /// 注意没有 "MainMenu" 会话:游戏唯一创建它的 PreloadManager.LoadMainMenuAssets 零调用者,
    /// 菜单资产实际由 "Common" 会话加载(LoadCommonAndMainMenuAssets),但那发生在
    /// ExecuteDeferred(=条的 1.0 完成点与 Freeze 点)之后、条 2 秒弥留期内——若映射它,
    /// postfix 会把已显示 1.0「完成」的条拽回 0.88。启动边界定在菜单就绪,延迟资产
    /// 属启动后后台工作,不进条也不进冻结的 Api 数据(todo#4,2026-08-27)。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, (float Start, float End)> SessionRanges =
        new()
        {
            { "IntroLogo", (0.66f, 0.70f) },
            { "MainMenuEssentials", (0.70f, 0.82f) },
        };

    // ConditionalWeakTable:session 结束后可被 GC,不阻止回收;值对象复用,无 per-frame 分配
    private sealed class SessionStat
    {
        public int Total;
        public long FirstTicks, LastTicks;
        public bool Recorded;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SessionStat> SessionStats = new();

    private static void PatchBootPhases()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.boot");
        foreach (var (type, method, label, progress) in Steps)
        {
            var mi = AccessTools.Method(AccessTools.TypeByName(type), method);
            if (mi == null)
            {
                Log.Warn($"[ItsLoading] step not found, skipped: {type}.{method}");
                continue;
            }
            StepMap[mi] = (label, progress);
            harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(StepPrefix)));
        }
        Log.Warn($"[ItsLoading] step patches installed ({StepMap.Count}/{Steps.Length})");

        var menu = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:LaunchMainMenu");
        if (menu != null)
        {
            harmony.Patch(menu, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeMainMenu)));
            Log.Warn("[ItsLoading] LaunchMainMenu patch installed");
        }

        // 资产会话:每帧真实进度(主线程 _Process 泵,帧自然流动,无需 ForceDraw)
        var sessionProcess = AccessTools.Method(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.AssetLoadingSession"), "Process");
        if (sessionProcess != null)
        {
            harmony.Patch(sessionProcess,
                postfix: new HarmonyMethod(typeof(ItsLoading), nameof(AfterSessionProcess)));
            Log.Warn("[ItsLoading] AssetLoadingSession.Process patch installed");
        }

        var logoPlay = AccessTools.Method(
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NLogoAnimation:PlayAnimation");
        if (logoPlay != null)
        {
            harmony.Patch(logoPlay, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeLogoPlay)));
            Log.Warn("[ItsLoading] PlayAnimation patch installed");
        }

        var loadMenu = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:LoadMainMenu");
        if (loadMenu != null)
        {
            harmony.Patch(loadMenu, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeLoadMenu)));
            Log.Warn("[ItsLoading] LoadMainMenu patch installed");
        }

        var deferred = AccessTools.Method("MegaCrit.Sts2.Core.Helpers.OneTimeInitialization:ExecuteDeferred");
        if (deferred != null)
        {
            harmony.Patch(deferred, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeDeferred)));
            Log.Warn("[ItsLoading] ExecuteDeferred patch installed");
        }
    }

    // ---- 资产会话 postfix:反射读队列实时状态 ----

    private static System.Reflection.FieldInfo _fName, _fToLoad, _fLoading, _fFinalizing, _fVfx, _fVfxLoading, _fTotal;

    private static void CacheSessionFields(Type t)
    {
        _fName = AccessTools.Field(t, "_name");
        _fToLoad = AccessTools.Field(t, "_toLoad");
        _fLoading = AccessTools.Field(t, "_loading");
        _fFinalizing = AccessTools.Field(t, "_finalizing");
        _fVfx = AccessTools.Field(t, "_vfxScenes");
        _fVfxLoading = AccessTools.Field(t, "_vfxLoading");
        _fTotal = AccessTools.Field(t, "_totalLoaded");
    }

    private static int Count(object queue) => (queue as System.Collections.ICollection)?.Count ?? 0;

    private static void AfterSessionProcess(object __instance)
    {
        try
        {
            if (_fName == null) CacheSessionFields(__instance.GetType());

            string name = _fName?.GetValue(__instance) as string ?? "";
            if (!SessionRanges.TryGetValue(name, out var range)) return;

            if (!UiOk || _barDead) return;

            int remaining = Count(_fToLoad?.GetValue(__instance))
                          + Count(_fLoading?.GetValue(__instance))
                          + Count(_fFinalizing?.GetValue(__instance))
                          + Count(_fVfx?.GetValue(__instance))
                          + ((_fVfxLoading?.GetValue(__instance) is true) ? 1 : 0);
            int loaded = _fTotal?.GetValue(__instance) as int? ?? 0;
            long nowTicks = Sw.ElapsedTicks;

            // 会话统计:首见记总数与起点,每帧只更新 LastTicks(一次字段写,零分配)
            SessionStat stat = SessionStats.GetValue(__instance, _ => new SessionStat());
            if (stat.Total <= 0)
            {
                stat.Total = loaded + remaining;
                stat.FirstTicks = nowTicks;
            }
            stat.LastTicks = nowTicks;
            if (stat.Total > 0 && remaining == 0 && !stat.Recorded)
            {
                stat.Recorded = true;
                Recorder.SessionSpans.Add(new Api.LoadSpan(
                    name, Api.LoadPhase.AssetSession,
                    Recorder.ToEngineMs(stat.FirstTicks),
                    (stat.LastTicks - stat.FirstTicks) * Recorder.SwTicksToMs,
                    $"{loaded}/{stat.Total}"));
            }

            // 除零防护(todo#5):会话首见时可能 loaded 与 remaining 同时为 0(资产批量
            // 加载失败时 AssetLoadingSession.Process 会静默丢弃非 Ok 的请求、一调用内
            // 清空完成)→ 0/0 = NaN;Clamp(NaN) 仍是 NaN、赋给 _fill.Size 不抛异常,
            // 填充条几何退化直到下一次有效 SetProgress。空会话按已完成处理。
            float local = stat.Total > 0 ? 1f - remaining / (float)stat.Total : 1f;
            float frac = range.Start + (range.End - range.Start) * local;
            SetProgress(frac, I18n.T("bar.assets", new() { ["name"] = name }), I18n.T("bar.assetsCount", new() { ["n"] = $"{loaded}/{stat.Total}" }), forceDraw: false);
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to read session state: {e}");
        }
    }

    private static void BeforeLogoPlay()
    {
        SetProgress(0.82f, I18n.T("bar.logo"), "");
    }

    private static void BeforeLoadMenu()
    {
        SetProgress(0.88f, I18n.T("bar.menuIn"), "");
    }

    private static void StepPrefix(MethodBase __originalMethod)
    {
        if (!StepMap.TryGetValue(__originalMethod, out var s)) return;
        // 步骤级耗时:本步骤起点到下一检查点(相邻差分,无额外采样)
        long nowTicks = Sw.ElapsedTicks;
        if (Recorder.StepSpans.Count > 0 && Recorder.LastStepTicks >= 0)
        {
            var prev = Recorder.StepSpans[^1];
            Recorder.StepSpans[^1] = prev with
            {
                DurationMs = (nowTicks - Recorder.LastStepTicks) * Recorder.SwTicksToMs,
            };
        }
        Recorder.LastStepTicks = nowTicks;
        Recorder.StepSpans.Add(new Api.LoadSpan(
            s.Label, Api.LoadPhase.BootStep, Recorder.ToEngineMs(nowTicks), 0, ""));
        SetProgress(s.Progress, I18n.T(s.Label), $"+{Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>logo/云同步后的启动收尾(LaunchMainMenu 调用点已逆向确认)。只处理一次。</summary>
    private static void BeforeMainMenu()
    {
        if (_menuHandled) return;
        _menuHandled = true;
        SetProgress(0.66f, I18n.T("bar.opening"), $"+{Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>主菜单已显示(ExecuteDeferred 语义):收尾最后一个步骤 span,冻结数据,移除 UI。</summary>
    private static void BeforeDeferred()
    {
        // 关闭最后一个挂起的启动步骤 span
        if (Recorder.StepSpans.Count > 0 && Recorder.LastStepTicks >= 0)
        {
            long nowTicks = Sw.ElapsedTicks;
            var prev = Recorder.StepSpans[^1];
            Recorder.StepSpans[^1] = prev with
            {
                DurationMs = (nowTicks - Recorder.LastStepTicks) * Recorder.SwTicksToMs,
            };
        }

        if (Recorder.BootAnchorMsec >= 0)
        {
            Recorder.TotalBootMs = (long)Time.GetTicksMsec() - Recorder.BootAnchorMsec;
        }
        else
        {
            // 首启兜底(todo#6):注入发生在 mod 加载期、autoload 已解析完,gd 节点
            // 不存在 → BootAnchorMsec=-1。原逻辑跳过赋值 → TotalBootMs=-1 → 瀑布图
            // 标题"-0.0s total"、total=Max(1.0,-1)=1ms、所有条形定位到屏幕外。
            // 兜底用 0 锚点:TotalBootMs=引擎至今总时长;span 的 StartMs 本就是绝对
            // 引擎毫秒,÷total 的分数定位自然正确(prelude 段无数据,从 mods 起)。
            Recorder.TotalBootMs = (long)Time.GetTicksMsec();
        }
        Api.LoadingDurations.Freeze();

        // 首启兜底注册瀑布图入口:见 WaterfallViewer.RegisterInBaseLib 的注释
        // (此刻 BaseLib 必已加载,LoadedMods 检查在内)
        Run("register waterfall at menu", WaterfallViewer.RegisterInBaseLib);

        // 一行启动摘要(诊断用,常量级日志)
        if (Recorder.ModSpans.Count > 0)
        {
            var top = new System.Text.StringBuilder("[ItsLoading] boot ");
            top.Append(Recorder.TotalBootMs.ToString("F0")).Append("ms")
               .Append($" (prefix={Recorder.PrefixCalls} postfix={Recorder.ModSpans.Count})")
               .Append("; slowest mods:");
            foreach (var s in Recorder.ModSpans
                         .OrderByDescending(m => m.DurationMs)
                         .ThenBy(m => m.Id, StringComparer.Ordinal)
                         .Take(3))
            {
                top.Append(' ').Append(s.Id).Append('=').Append(s.DurationMs.ToString("F0")).Append("ms");
            }
            Log.Info(top.ToString());
        }

        SetProgress(1.0f, I18n.T("bar.done"), $"{Sw.ElapsedMilliseconds}ms");
        var tree = (SceneTree)Engine.GetMainLoop();
        var timer = tree.CreateTimer(2.0);
        timer.Timeout += () => Run("remove bar", () =>
        {
            _barDead = true;
            _layer?.QueueFree();
            BootSplash.Takeover(); // C# 条移除时才隐藏 gd splash
        });
    }
}
