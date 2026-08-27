using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

// ---------------------------------------------------------------- 瀑布图查看器(独立于启动路径)
//
// 架构拆分 #1 从 ItsLoading.cs 原样搬出:菜单就绪后才可打开的调试 UI,
// 只读冻结后的 Api.LoadingDurations 数据,不参与启动路径。
// 对外接口:RegisterInBaseLib(BaseLib 软依赖注册)+ CompatHooks(垫片回调)。
// 类必须 public:ItsLoadingCompat.dll(另一程序集)经 CompatHooks 回调进来;
// 其余成员 internal/private,公开面就这一个回调入口。

public static class WaterfallViewer
{
    private static CanvasLayer _waterfallLayer; // 瀑布图层(打开期间兼作热键阻断屏)
    private static bool _wfRegistered;          // 瀑布图入口是否已注册(防重复)

    /// <summary>
    /// BaseLib 已加载时注册(常规路径 = AfterModLoad 观察到 BaseLib 加载完成;
    /// 兜底路径 = 菜单就绪时补注册)。BaseLib 的配置体系:SimpleModConfig 子类 +
    /// [ConfigButton] 方法;行标签 = 方法名(本地化缺失时原文回退)。
    /// 软依赖实现:编译期引用 refs/BaseLib.dll(不入库),注册调用放在
    /// 本方法里 —— BaseLib 缺席时它永不被调用、WaterfallConfig 类型永不加载
    /// (JIT 按方法惰性解析),不影响本 mod。
    /// BaseLib 类型只存在于独立的兼容垫片 ItsLoadingCompat.dll 中 —— 主 dll
    /// 绝不引用 BaseLib(否则 ModManager 的 assembly.GetTypes() 会在 BaseLib
    /// 未加载时抛 ReflectionTypeLoadException,v0.11.0 的翻车根源),垫片在此刻
    /// 手动 LoadFrom,类型解析必然成功。
    /// 首启时我们排在队尾,BaseLib 早在补丁安装前加载完,AfterModLoad 不可能
    /// 观察到它 —— 没有兜底的话瀑布图入口要等到第二次启动才存在(2026-08-27)。
    /// </summary>
    internal static void RegisterInBaseLib()
    {
        if (_wfRegistered) return;
        if (!ModManager.GetLoadedMods().Any(m => m.manifest?.id == "BaseLib"))
        {
            Log.Warn("[ItsLoading] BaseLib not loaded — waterfall entry skipped");
            return;
        }
        string shimPath = Path.Combine(
            Path.GetDirectoryName(typeof(ItsLoading).Assembly.Location) ?? ".", "ItsLoadingCompat.dll");
        if (!File.Exists(shimPath))
        {
            Log.Warn("[ItsLoading] ItsLoadingCompat.dll not found — BaseLib entry skipped");
            return;
        }
        var shim = Assembly.LoadFrom(shimPath);
        shim.GetType("ItsLoadingCompat.Entry")?
            .GetMethod("Register")?
            .Invoke(null, new object[] { "ItsLoading" });
        _wfRegistered = true;
        Log.Warn("[ItsLoading] waterfall entry registered in BaseLib (via shim)");
    }

    /// <summary>兼容垫片回调入口(ItsLoadingCompat.Entry 经反射回调)。</summary>
    public static class CompatHooks
    {
        public static void OpenWaterfall() => Show();
    }

    private static void Show()
    {
        ItsLoading.Run("show waterfall", () =>
        {
            // toggle:再按一次 = 关闭
            if (_waterfallLayer != null)
            {
                Close();
                return;
            }
            // 玩家可能在本次会话内切换过语言(SettingsSave.Language 是实时值)——
            // 进度条阶段的表在启动时加载,瀑布图打开时重读一次(懒刷新)。
            I18n.Init();
            var tree = (SceneTree)Engine.GetMainLoop();
            Vector2 vs = tree.Root.GetVisibleRect().Size;

            _waterfallLayer = new CanvasLayer { Layer = 1200 };

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.92f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed)
                {
                    Close();
                }
            };
            _waterfallLayer.AddChild(dim);

            var title = new Label
            {
                Text = Api.LoadingDurations.IsReady
                    ? I18n.T("wf.title", new() { ["s"] = (Api.LoadingDurations.TotalBootMs / 1000.0).ToString("F1") })
                    : I18n.T("wf.notReady"),
            };
            title.Position = new Vector2(48f, 24f);
            title.AddThemeFontSizeOverride("font_size", 24);
            _waterfallLayer.AddChild(title);

            var close = new Button { Text = I18n.T("wf.close") };
            close.Position = new Vector2(vs.X - 180f, 24f);
            close.Pressed += Close;
            _waterfallLayer.AddChild(close);

            if (Api.LoadingDurations.IsReady)
            {
                BuildWaterfallChart(_waterfallLayer, vs);
            }

            tree.Root.AddChild(_waterfallLayer);

            // 输入接入游戏的热键栈(NHotkeyManager 挂在 NGame,菜单/局内常驻——设置页
            // 的 TabLeft/TabRight 也走它;capstone 容器只在局内存在,菜单下是 null,
            // 2026-08-27 实测)。阻断屏压住背后全部热键(模态语义),再压 cancel→关闭:
            // LIFO 栈 + 命中即 SetInputAsHandled,ESC 不会再被背后设置页抢走;
            // IsActionPressed 匹配 NInputManager 再分发的动作名 → 自动跟随玩家改键与手柄。
            var hm = MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager.Instance;
            if (hm != null)
            {
                hm.AddBlockingScreen(_waterfallLayer);
                hm.PushHotkeyPressedBinding(
                    MegaCrit.Sts2.Core.ControllerInput.MegaInput.cancel, Close);
            }
            else
            {
                Log.Warn("[ItsLoading] NHotkeyManager unavailable — cancel key won't close waterfall");
            }
            Log.Info("[ItsLoading] waterfall opened");
        });
    }

    private static void Close()
    {
        var hm = MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager.Instance;
        if (hm != null)
        {
            hm.RemoveHotkeyPressedBinding(
                MegaCrit.Sts2.Core.ControllerInput.MegaInput.cancel, Close);
            hm.RemoveBlockingScreen(_waterfallLayer);
        }
        _waterfallLayer?.QueueFree();
        _waterfallLayer = null;
    }

    /// <summary>瀑布图行标签:mod 行用 manifest.name(与游戏 mod 列表一致),其余查 i18n 表。</summary>
    private static string WfRowLabel(Api.LoadSpan s)
    {
        string modName = ModManager.Mods.FirstOrDefault(m => m.manifest?.id == s.Id)
            ?.manifest?.name;
        if (!string.IsNullOrEmpty(modName)) return modName;
        return I18n.T(s.Id);
    }

    private static Color WfColor(Api.LoadPhase p) => p switch
    {
        Api.LoadPhase.Prelude => new Color(0.55f, 0.57f, 0.62f, 1f),
        Api.LoadPhase.ModLoad => new Color(0.20f, 0.85f, 0.90f, 1f),
        Api.LoadPhase.BootStep => new Color(0.95f, 0.70f, 0.25f, 1f),
        Api.LoadPhase.AssetSession => new Color(0.40f, 0.85f, 0.50f, 1f),
        _ => Colors.White,
    };

    /// <summary>
    /// 渲染层的空白填补(纯展示,不写入 Api 数据——公开 API 保持纯测量语义):
    /// ① 首行之前的空白:首启时我们可观测之前的「游戏预加载」段(引擎 C++ 初始化 +
    ///    Steam 读取 + 工坊扫描,C# 侧看不到),占位提示下次启动可获完整数据;
    /// ② prelude 行结束与首个 mod 行之间的窄缝:本 mod 自身的 dll 加载与 Init
    ///    (自身 TryLoadMod 的 prefix 装不上补丁,起点只能近似,可能留缝)。
    /// 两个填补都只在缝隙实际存在(>阈值)时出现。
    /// </summary>
    private static void FillWaterfallGaps(System.Collections.Generic.List<Api.LoadSpan> rows)
    {
        if (rows.Count == 0) return;
        var fills = new System.Collections.Generic.List<Api.LoadSpan>();
        double firstStart = rows[0].StartMs;
        if (firstStart > 100)
        {
            fills.Add(new Api.LoadSpan(I18n.T("wf.preBoot"), Api.LoadPhase.Prelude, 0, firstStart, ""));
        }
        double preludeEnd = double.MinValue;
        foreach (var r in rows)
        {
            if (r.Phase == Api.LoadPhase.Prelude)
                preludeEnd = Math.Max(preludeEnd, r.StartMs + r.DurationMs);
        }
        // 取「起点晚于 prelude 结束」的首个 mod 行:本 mod 自身行的起点(≈Init 开始)
        // 早于 prelude 结束(交接发生在 Init 中途),若取全表最早会让缝隙算成负数、
        // 填补永不触发(2026-08-27 用户实测 waterfall 中无填补行)。
        double firstModStart = double.MaxValue;
        foreach (var r in rows)
        {
            if (r.Phase == Api.LoadPhase.ModLoad && r.StartMs > preludeEnd && r.StartMs < firstModStart)
                firstModStart = r.StartMs;
        }
        Log.Info($"[ItsLoading] wf gaps: preludeEnd={preludeEnd:F0}ms " +
                 $"firstModAfterPrelude={(firstModStart < double.MaxValue ? firstModStart.ToString("F0") + "ms" : "none")} " +
                 $"gap={(firstModStart < double.MaxValue ? (firstModStart - preludeEnd).ToString("F0") + "ms" : "n/a")}");
        if (preludeEnd > double.MinValue && firstModStart < double.MaxValue &&
            firstModStart - preludeEnd > 20)
        {
            fills.Add(new Api.LoadSpan(I18n.T("wf.handoffGap"), Api.LoadPhase.ModLoad,
                preludeEnd, firstModStart - preludeEnd, ""));
        }
        if (fills.Count == 0) return;
        rows.AddRange(fills);
        rows.Sort((a, b) => a.StartMs != b.StartMs
            ? a.StartMs.CompareTo(b.StartMs)
            : b.DurationMs.CompareTo(a.DurationMs));
    }

    internal static void BuildWaterfallChart(Node parent, Vector2 vs)
    {
        double total = Math.Max(1.0, Api.LoadingDurations.TotalBootMs);

        // 汇总所有 span,按时间轴排序
        var rows = new System.Collections.Generic.List<Api.LoadSpan>();
        rows.AddRange(Api.LoadingDurations.Phases);
        rows.AddRange(Api.LoadingDurations.BootSteps);
        rows.AddRange(Api.LoadingDurations.AssetSessions);
        rows.AddRange(Api.LoadingDurations.ModLoads);
        rows.Sort((a, b) => a.StartMs != b.StartMs
            ? a.StartMs.CompareTo(b.StartMs)
            : b.DurationMs.CompareTo(a.DurationMs));
        FillWaterfallGaps(rows);

        // 滚动区(菜单阶段正常帧循环,Container 可用)
        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetTop = 80f;
        scroll.OffsetBottom = -40f;
        scroll.OffsetLeft = 48f;
        scroll.OffsetRight = -48f;
        parent.AddChild(scroll);

        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        scroll.AddChild(box);

        // 时间轴刻度(每 5s)
        var ruler = new Control
        {
            CustomMinimumSize = new Vector2(0, 22f),
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        box.AddChild(ruler);
        for (double t = 0; t <= total; t += 5000.0)
        {
            float frac = (float)(t / total);
            var tick = new Label { Text = $"{t / 1000.0:F0}s" };
            tick.AnchorLeft = frac;
            tick.AddThemeFontSizeOverride("font_size", 12);
            tick.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.45f));
            ruler.AddChild(tick);
            var line = new ColorRect { Color = new Color(1f, 1f, 1f, 0.08f) };
            line.AnchorLeft = frac;
            line.AnchorRight = frac;
            line.OffsetTop = 20f;
            line.OffsetBottom = 2000f;
            ruler.AddChild(line);
        }

        foreach (var s in rows)
        {
            var row = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(0, 18f),
                SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            };
            box.AddChild(row);

            var name = new Label
            {
                // 行标签(2026-08-27):Id 存 i18n key 或 mod id(公开 API 返回稳定 key/纯 id)。
                // 显示时:mod 行查 manifest.name(游戏的 mod 列表同款,中文名存在 manifest 里);
                // 非 mod 行查 i18n 表(step.atlas 等译出);都不命中原样透传。
                Text = $"{WfRowLabel(s)}  {s.DurationMs / 1000.0:F2}s",
                CustomMinimumSize = new Vector2(340f, 18f),
                ClipText = true,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            name.AddThemeColorOverride("font_color", WfColor(s.Phase));
            row.AddChild(name);

            var barArea = new Control
            {
                CustomMinimumSize = new Vector2(0, 18f),
                SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            };
            row.AddChild(barArea);

            float start = (float)(s.StartMs / total);
            float end = (float)Math.Min(1.0, (s.StartMs + s.DurationMs) / total);
            var bar = new ColorRect { Color = WfColor(s.Phase) };
            bar.AnchorLeft = start;
            bar.AnchorRight = Math.Max(end, start + 0.0015f);
            bar.AnchorTop = 0f;
            bar.AnchorBottom = 1f;
            bar.OffsetTop = 4f;
            bar.OffsetBottom = -4f;
            barArea.AddChild(bar);
        }
    }
}
