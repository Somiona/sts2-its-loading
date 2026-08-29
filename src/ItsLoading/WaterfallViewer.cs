using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

// ---------------------------------------------------------------- 瀑布图查看器(独立于启动路径)
//
// 菜单就绪后才可打开的调试 UI,只读冻结后的 Api.LoadingDurations 数据,
// 不参与启动路径。
// 对外接口:RegisterInBaseLib(BaseLib 软依赖注册)+ CompatHooks(垫片回调)。
// 类必须 public:ItsLoadingCompat.dll(另一程序集)经 CompatHooks 回调进来;
// 其余成员 internal/private,公开面就这一个回调入口。

public static class WaterfallViewer
{
    private static CanvasLayer _waterfallLayer; // 瀑布图层(打开期间兼作热键阻断屏)
    private static bool _wfRegistered;          // 瀑布图入口是否已注册(防重复)
    // 折叠(默认)= 隐藏工坊扫描逐项与 mod 内部子步骤两组细节行;mod 整体耗时保留。
    // 静态:会话内切换后记住,重开瀑布图保持上次选择。
    private static bool _wfDetailed;

    /// <summary>
    /// BaseLib 已加载时注册(常规路径 = AfterModLoad 观察到 BaseLib 加载完成;
    /// 兜底路径 = 菜单就绪时补注册)。BaseLib 的配置体系:SimpleModConfig 子类 +
    /// [ConfigButton] 方法;行标签 = 方法名(本地化缺失时原文回退)。
    /// 软依赖实现:编译期引用 refs/BaseLib.dll(不入库),注册调用放在
    /// 本方法里 —— BaseLib 缺席时它永不被调用、WaterfallConfig 类型永不加载
    /// (JIT 按方法惰性解析),不影响本 mod。
    /// BaseLib 类型只存在于独立的兼容垫片 ItsLoadingCompat.dll 中 —— 主 dll
    /// 绝不引用 BaseLib(否则 ModManager 的 assembly.GetTypes() 会在 BaseLib
    /// 未加载时抛 ReflectionTypeLoadException),垫片在此刻
    /// 手动 LoadFrom,类型解析必然成功。
    /// 首启时我们排在队尾,BaseLib 早在补丁安装前加载完,AfterModLoad 不可能
    /// 观察到它 —— 没有兜底的话瀑布图入口要等到第二次启动才存在。
    /// </summary>
    internal static void RegisterInBaseLib()
    {
        if (_wfRegistered) return;
        if (!ModManager.GetLoadedMods().Any(m => m.manifest?.id == "BaseLib"))
        {
            Log.Warn("[ItsLoading] BaseLib not loaded — waterfall entry skipped");
            return;
        }
        InstallShimResolver();
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
            .Invoke(null, new object[] { ItsLoading.ModId });
        _wfRegistered = true;
        Log.Warn("[ItsLoading] waterfall entry registered in BaseLib (via shim)");
    }

    /// <summary>
    /// 垫片依赖的显式解析兜底。游戏的 HandleAssemblyResolveFailure 只兜 sts2/0Harmony,
    /// 垫片引用的 BaseLib 全链无人解析:Wine 下实测 AfterModLoad 早注册路径中
    /// JIT 编译 Entry.Register 时按全名绑定「已加载的」BaseLib 失败
    /// (FileNotFoundException,报错穿过 Harmony/MonoMod 的 JIT 钩子)。挂 Default ALC
    /// 的 Resolving,按简单名返回已加载实例——绑定必成,与平台/加载顺序无关;
    /// macOS 正常路径探测本就命中,此兜底不触发,零影响。
    /// </summary>
    private static bool _shimResolverInstalled;
    private static void InstallShimResolver()
    {
        if (_shimResolverInstalled) return;
        _shimResolverInstalled = true;
        AssemblyLoadContext.Default.Resolving += (alc, name) =>
        {
            if (name.Name != "BaseLib" && name.Name != "ItsLoading") return null;
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name.Name);
            if (loaded != null)
                Log.Warn($"[ItsLoading] resolved {name.Name} via fallback (requested {name.FullName})");
            return loaded;
        };
    }

    /// <summary>兼容垫片回调入口(ItsLoadingCompat.Entry 经反射回调)。</summary>
    public static class CompatHooks
    {
        public static void OpenWaterfall() => Show();

        /// <summary>BaseLib 设置页下拉框的静态属性透传:getter 直读 cfg 文件。</summary>
        public static LoadingTheme GetTheme() => ThemeRegistry.Current();

        /// <summary>下拉框选中:setter 同步写 cfg(见 ThemeRegistry.TrySet)。</summary>
        public static void SetTheme(LoadingTheme theme) => ThemeRegistry.TrySet(theme);
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
            // 阶段埋点:Wine 下原生崩溃无托管异常可捕(点开即死、
            // "waterfall opened" 未达)。逐阶段一行日志,复现时最后一行即崩溃点。
            Log.Warn("[ItsLoading] wf stage 1: i18n reload");
            // 玩家可能在本次会话内切换过语言(SettingsSave.Language 是实时值)——
            // 进度条阶段的表在启动时加载,瀑布图打开时重读一次(懒刷新)。
            I18n.Init();
            var tree = (SceneTree)Engine.GetMainLoop();
            Vector2 vs = tree.Root.GetVisibleRect().Size;

            Log.Warn("[ItsLoading] wf stage 2: layer + dim");
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

            // 折叠/详细:切换后整体重建(Show 的 toggle 语义天然支持)
            var detail = new Button
            {
                Text = _wfDetailed ? I18n.T("wf.detailed") : I18n.T("wf.collapsed"),
            };
            detail.Position = new Vector2(vs.X - 380f, 24f);
            detail.Pressed += () =>
            {
                _wfDetailed = !_wfDetailed;
                Close();
                Show();
            };
            _waterfallLayer.AddChild(detail);

            if (Api.LoadingDurations.IsReady)
            {
                Log.Warn("[ItsLoading] wf stage 3: chart build");
                BuildWaterfallChart(_waterfallLayer, vs);
            }

            Log.Warn("[ItsLoading] wf stage 4: attach to tree");
            tree.Root.AddChild(_waterfallLayer);

            // 输入接入游戏的热键栈(NHotkeyManager 挂在 NGame,菜单/局内常驻——设置页
            // 的 TabLeft/TabRight 也走它;capstone 容器只在局内存在,菜单下是 null)。
            // 阻断屏压住背后全部热键(模态语义),再压 cancel→关闭:
            // LIFO 栈 + 命中即 SetInputAsHandled,ESC 不会再被背后设置页抢走;
            // IsActionPressed 匹配 NInputManager 再分发的动作名 → 自动跟随玩家改键与手柄。
            var hm = MegaCrit.Sts2.Core.Nodes.CommonUi.NHotkeyManager.Instance;
            if (hm != null)
            {
                Log.Warn("[ItsLoading] wf stage 5: hotkey bindings");
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
        // 子步骤行:mod 名 + 子步骤(init 类型 / pck 文件),与所属 mod 行区分
        if (s.Phase == Api.LoadPhase.ModSubStep)
            return $"{modName ?? s.Id} · {s.Detail}";
        // 工坊扫描行:Detail = mod 显示名(首启或未观测到时为空,走 id 回退)
        if (s.Phase == Api.LoadPhase.Prelude && !string.IsNullOrEmpty(s.Detail))
            return s.Detail;
        if (!string.IsNullOrEmpty(modName)) return modName;
        return I18n.T(s.Id);
    }

    private static Color WfColor(Api.LoadPhase p) => p switch
    {
        Api.LoadPhase.Prelude => new Color(0.55f, 0.57f, 0.62f, 1f),
        Api.LoadPhase.ModLoad => new Color(0.20f, 0.85f, 0.90f, 1f),
        Api.LoadPhase.ModSubStep => new Color(0.20f, 0.85f, 0.90f, 0.55f),
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
        // 填补永不触发。
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
        // 时间轴跨度 = 总时长 + 3s 尾部空白:滚动到头时最后一个条与刻度标签
        // 完整可见,且终点之后留有呼吸空间。条的锚定分母一律用 span,
        // 而不是 total——空白是锚定区之外的固有留白。
        double span = total + 3000.0;

        // 汇总所有 span,按时间轴排序
        var rows = new System.Collections.Generic.List<Api.LoadSpan>();
        rows.AddRange(Api.LoadingDurations.Phases);
        rows.AddRange(Api.LoadingDurations.BootSteps);
        rows.AddRange(Api.LoadingDurations.AssetSessions);
        rows.AddRange(Api.LoadingDurations.ModLoads);
        if (_wfDetailed)
        {
            rows.AddRange(Api.LoadingDurations.ModSubSteps);
            rows.AddRange(Api.LoadingDurations.WorkshopItems);
        }
        rows.Sort((a, b) => a.StartMs != b.StartMs
            ? a.StartMs.CompareTo(b.StartMs)
            : b.DurationMs.CompareTo(a.DurationMs));
        FillWaterfallGaps(rows);

        // 条形区宽度:每屏 37.5s,超出可视宽度即横向滚动。
        // 「每屏」按右栏可视宽度校准:总宽 - 左右边距 - 名称列 - 纵向滚动条。
        const double secondsPerScreen = 37.5;
        const float nameColW = 340f;
        double usable = Math.Max(320.0, vs.X - 96.0 - nameColW - 16.0);
        float timelineW = (float)Math.Max(usable, usable * span / 1000.0 / secondsPerScreen);

        // 双栏滚动(菜单阶段正常帧循环,Container 可用):左名称列独立面板、
        // 禁横向滚动 → 始终可见;横向滚动只作用于右栏条形区。两栏纵向滚动同步。
        var split = new HBoxContainer();
        split.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        split.OffsetTop = 80f;
        split.OffsetBottom = -40f;
        split.OffsetLeft = 48f;
        split.OffsetRight = -48f;
        parent.AddChild(split);

        var leftScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(nameColW, 0f),
            SizeFlagsVertical = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
        };
        split.AddChild(leftScroll);
        var leftBox = new VBoxContainer
        {
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        leftScroll.AddChild(leftBox);
        // 顶部垫块:与右栏的 22px 刻度行对齐,行序一一对应
        leftBox.AddChild(new Control { CustomMinimumSize = new Vector2(0f, 22f) });

        var rightScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            SizeFlagsVertical = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        split.AddChild(rightScroll);

        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        rightScroll.AddChild(box);

        // 纵向同步(任一侧滚动都带动另一侧;同值写入不触发事件,无递归)
        bool syncing = false;
        leftScroll.GetVScrollBar().ValueChanged += v =>
        {
            if (syncing) return;
            syncing = true;
            rightScroll.ScrollVertical = (int)v;
            syncing = false;
        };
        rightScroll.GetVScrollBar().ValueChanged += v =>
        {
            if (syncing) return;
            syncing = true;
            leftScroll.ScrollVertical = (int)v;
            syncing = false;
        };

        // 时间轴刻度(每 5s),锚在右栏条形区内(左栏就是名称列,天然与条对齐)。
        var ruler = new Control
        {
            CustomMinimumSize = new Vector2(timelineW, 22f),
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        box.AddChild(ruler);
        // 竖线高度按实际行数算:行数随 mod/子步骤规模变化,写死 2000px 会在
        // 长列表中途断掉。
        float lineH = 20f + rows.Count * 18f + 40f;
        for (double t = 0; t <= total; t += 5000.0)
        {
            float frac = (float)(t / span);
            var tick = new Label { Text = $"{t / 1000.0:F0}s" };
            tick.AnchorLeft = frac;
            tick.AddThemeFontSizeOverride("font_size", 12);
            tick.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.45f));
            ruler.AddChild(tick);
            var line = new ColorRect { Color = new Color(1f, 1f, 1f, 0.08f) };
            line.AnchorLeft = frac;
            line.AnchorRight = frac;
            line.OffsetTop = 20f;
            line.OffsetBottom = lineH;
            ruler.AddChild(line);
        }

        foreach (var s in rows)
        {
            var row = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(timelineW, 18f),
                SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            };
            box.AddChild(row);

            var name = new Label
            {
                // 行标签:Id 存 i18n key 或 mod id(公开 API 返回稳定 key/纯 id)。
                // 显示时:mod 行查 manifest.name(游戏的 mod 列表同款,中文名存在 manifest 里);
                // 非 mod 行查 i18n 表(step.atlas 等译出);都不命中原样透传。
                Text = $"{WfRowLabel(s)}  {s.DurationMs / 1000.0:F2}s",
                CustomMinimumSize = new Vector2(nameColW, 18f),
                ClipText = true,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            name.AddThemeColorOverride("font_color", WfColor(s.Phase));
            leftBox.AddChild(name);

            var barArea = new Control
            {
                CustomMinimumSize = new Vector2(timelineW, 18f),
                SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            };
            row.AddChild(barArea);

            // 数值加固:锚点必须落在 [0,1] 且非 NaN——负 StartMs(gd 锚点交接缝)或
            // 病态时长直接进 Godot 原生布局,在 Wine 渲染栈上是潜在原生崩溃源。
            float start = (float)Math.Clamp(s.StartMs / span, 0.0, 1.0);
            float end = (float)Math.Clamp((s.StartMs + s.DurationMs) / span, 0.0, 1.0);
            if (float.IsNaN(start) || float.IsNaN(end) || end < start)
            {
                Log.Warn($"[ItsLoading] wf skip bad span: {s.Id} start={s.StartMs:F0}ms dur={s.DurationMs:F0}ms");
                continue;
            }
            Color baseColor = WfColor(s.Phase);
            // 悬浮联动高亮(左标签 ↔ 右条,双向):描边 = 同锚定、四向外扩的同色
            // 矩形垫在条下,条压住中心只露出 2px 边;标签侧向白色轻混 35%。
            var halo = new ColorRect
            {
                Color = baseColor,
                Visible = false,
                // 纯视觉:不吃鼠标。否则指在条内部时悬浮目标是条自身而非
                // barArea,联动高亮只在条外沿触发。
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            halo.AnchorLeft = start;
            halo.AnchorRight = Math.Max(end, start + 0.0015f);
            halo.AnchorTop = 0f;
            halo.AnchorBottom = 1f;
            halo.OffsetLeft = -2f;
            halo.OffsetRight = 2f;
            halo.OffsetTop = 1f;
            halo.OffsetBottom = -1f;
            barArea.AddChild(halo);

            var bar = new ColorRect { Color = baseColor, MouseFilter = Control.MouseFilterEnum.Ignore };
            bar.AnchorLeft = start;
            bar.AnchorRight = Math.Max(end, start + 0.0015f);
            bar.AnchorTop = 0f;
            bar.AnchorBottom = 1f;
            bar.OffsetTop = 4f;
            bar.OffsetBottom = -4f;
            barArea.AddChild(bar);

            Color hoverColor = baseColor.Lerp(Colors.White, 0.35f);
            void SetHover(bool on)
            {
                halo.Visible = on;
                name.AddThemeColorOverride("font_color", on ? hoverColor : baseColor);
            }
            name.MouseFilter = Control.MouseFilterEnum.Stop; // Label 默认忽略鼠标
            name.MouseEntered += () => SetHover(true);
            name.MouseExited += () => SetHover(false);
            barArea.MouseEntered += () => SetHover(true);
            barArea.MouseExited += () => SetHover(false);
        }
    }
}
