using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- Minespire 主题(Minecraft 风格)
//
// 整屏 Mojang 红的居中布局:标签在条上方 ×2、左下活动日志、右下奔跑狐狸。
// 布局与颜色取自 Minecraft 加载画面(实测值来自 NeoForge/FancyModLoader 的 default 主题):
//   · 854×480 设计矩形等比缩放居中;条 = 2px 白描边 + 内缩 4px 白填充
//     (progress_bar_bg/fg 本是纯二色像素画,StyleBoxFlat+ColorRect 无损复刻)
//   · 狐狸 fox_running.png 28 帧竖排(© NeoForged contributors, LGPL-2.1)
// 本类是首启/桥版本不匹配时的 C# 兜底;正常路径的同一布局由 gd 模板
// (BootSplash.cs,经 @@MS_*@@ token 与本类常量同源)从帧 0 呈现。
// 仍守经典主题约束:不用 Container、全手动定位;揭幕为淡出(菜单就绪后
// 有自然帧,tween 可靠推进)。

internal sealed class MinespireBar : ILoadingTheme
{
    // ---- Minespire 主题常量(gd 模板经 token 消费,勿在模板写死) ----
    internal static readonly Color BgColor = new(0.937f, 0.196f, 0.239f, 1f); // #ef323d
    internal static readonly Color TextColor = new(1f, 1f, 1f, 1f);
    internal static readonly Color DimTextColor = new(1f, 1f, 1f, 0.8f);

    internal const float DesignW = 854f, DesignH = 480f;
    internal const float BarWidth = 400f, BarHeight = 20f;
    internal const float BarCenterX = DesignW / 2f;
    internal const float BarsTop = 250f;     // 条块顶;标签依次向上/向下排
    internal const float LabelGap = 4f;      // 标签 ↔ 条
    internal const float BarGap = 5f;        // 条 ↔ 下一标签
    internal const float StepLabelH = 26f;   // 20px 字号行高(设计坐标)
    internal const float DetailLabelH = 19f; // 14px 字号行高
    internal const float BorderWidth = 2f;   // 描边宽(nine-slice bg 边)
    internal const float FillInset = 4f;     // 填充内缩(nine-slice fg 边)
    internal const float LogoY = 96f;        // logo 顶部(素材缺席时回退同位文字标题)
    internal const float LogoDesignW = 520f; // logo 设计宽(高按图比例自适应)
    internal const int FallbackTitleFont = 28;
    internal const int StepFont = 20, DetailFont = 14;
    internal const float LogLeft = 10f, LogBottom = 10f;
    internal const int LogLines = 10, LogFont = 12;
    internal const float LogLineH = 17f;
    internal const float FoxW = 151f, FoxH = 128f;
    internal const int FoxFrames = 28;
    internal const float FoxFps = 12f;
    internal const float FoxRight = 10f, FoxBottom = 30f; // 版本号上方
    internal const float VersionRight = 10f, VersionBottom = 10f;
    internal const float IndeterminateCycleSeconds = 3f;  // 滑块跑满一程
    internal const float FadeSeconds = 0.4f;              // 揭幕淡出

    private CanvasLayer _layer;
    private Control _root;      // 全屏根:淡出对它做 modulate(CanvasLayer 无此属性)
    private Label _stepLabel;   // 总体条上方:当前步骤 + 计数
    private Label _detailLabel; // 阶段条上方:当前对象 + 耗时
    private ColorRect _overallFill;
    private ColorRect _localFill;
    private readonly List<Label> _logLabels = new(LogLines);
    private readonly List<string> _logLines = new(LogLines);
    private string _lastLog = "";
    private Animator _animator; // 狐狸逐帧 + 不定进度滑块(自然帧驱动,突发期自动冻结)
    private float _maxFillW;    // 填充最大宽(内缩后的条宽,双条同几何)
    private AtlasTexture _foxAtlas;
    private float _scale;       // 854×480 设计矩形 → 屏幕的等比缩放
    private Vector2 _origin;    // 设计矩形在屏幕上的左上角

    private bool UiOk => _layer != null;
    private bool _barDead;      // 同 ClassicBar:退休后挡住仍可能触发的 postfix Present

    public void Build()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 vs = tree.Root.GetVisibleRect().Size;
        _scale = Math.Min(vs.X / DesignW, vs.Y / DesignH);
        _origin = new Vector2((vs.X - DesignW * _scale) / 2f, (vs.Y - DesignH * _scale) / 2f);

        _layer = new CanvasLayer { Layer = 999 };
        _root = new Control();
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // 整屏覆盖期菜单仍可交互:纯视觉覆盖,全家不吃输入
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;
        _layer.AddChild(_root);

        var bg = new ColorRect { Color = BgColor, MouseFilter = Control.MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(bg);

        AddLogo();

        // 条块:step 标签 → 总体条 → detail 标签 → 阶段条(Minecraft 风格 labelGap/barGap 流)
        float barX = BarCenterX - BarWidth / 2f;
        float y = BarsTop;
        _stepLabel = AddLabel(P(barX, y), StepFont, TextColor);
        y += StepLabelH + LabelGap;
        AddBar(P(barX, y), out _overallFill);
        _overallFill.Color = new Color(1f, 1f, 1f, 0.75f); // 总体条略淡,主视觉留给阶段条
        y += BarHeight + BarGap;
        _detailLabel = AddLabel(P(barX, y), DetailFont, DimTextColor);
        y += DetailLabelH + LabelGap;
        AddBar(P(barX, y), out _localFill);

        // 活动日志:左下角,最新在底部,越旧越淡(gd _log_line 同款契约)
        for (int i = 0; i < LogLines; i++)
        {
            var l = AddLabel(
                P(LogLeft, DesignH - LogBottom - (LogLines - i) * LogLineH),
                LogFont, TextColor);
            float a = 0.3f + 0.65f * (i + 1) / LogLines;
            l.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, a));
            _logLabels.Add(l);
        }

        AddFox();
        AddVersionLabel();

        // 动画器与狐狸无关也常在:不定进度滑块也靠它(Indeterminate 后 Present 接线)
        _animator = new Animator
        {
            Fox = _foxAtlas,
            LocalFill = _localFill,
            FillBaseX = S(FillInset),
            SlabTravelW = _maxFillW * 3f / 4f, // 滑块宽 = 最大宽 1/4
            FoxFrameH = FoxH,
            FoxFps = FoxFps,
            FoxFrames = FoxFrames,
        };
        _root.AddChild(_animator);

        // 首次注入提示(左上角,不挡布局)
        if (BootSplash.InjectedThisRun)
        {
            var hint = AddLabel(new Vector2(24f, 24f), 14, TextColor);
            hint.Text = I18n.T("hint.injected");
            hint.AddThemeColorOverride("font_color", new Color(0.62f, 0.64f, 0.70f, 1f));
            var t = tree.CreateTimer(8.0);
            t.Timeout += () => ItsLoading.Run("hide injection hint",
                () => { if (GodotObject.IsInstanceValid(hint)) hint.QueueFree(); });
        }

        // 必须直接 AddChild(同步突发期间 deferred 队列不执行),首帧内容随 BeginMods 呈现
        tree.Root.AddChild(_layer);
        Log.Warn($"[ItsLoading] minespire bar attached (viewport {vs.X}x{vs.Y}, scale {_scale:0.##})");
    }

    /// <summary>
    /// MC 风格游戏 logo(设计宽 520、高按图比例,水平居中);素材缺席回退文字标题
    /// ——主题不因缺图失败。
    /// </summary>
    private void AddLogo()
    {
        ImageTexture tex = LoadThemeTexture("mc_style_sts2_logo.png");
        if (tex == null)
        {
            var title = new Label
            {
                Text = "SLAY THE SPIRE 2",
                Position = P(0f, LogoY),
                Size = new Vector2(S(DesignW), S(FallbackTitleFont + 6f)),
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            title.AddThemeFontSizeOverride("font_size", F(FallbackTitleFont));
            title.AddThemeColorOverride("font_color", TextColor);
            _root.AddChild(title);
            return;
        }
        float w = S(LogoDesignW);
        float h = w * tex.GetHeight() / Math.Max(1, tex.GetWidth());
        var logo = new TextureRect
        {
            // 钳制陷阱:默认 KEEP_SIZE 的最小尺寸=贴图尺寸,Texture/
            // Position 赋值把控件顶到该最小尺寸后,Size 再赋也会被旧 min 钳住
            // (2046px logo 原样进小窗口)。初始化器按源序赋值,ExpandMode 必须首项。
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            Texture = tex,
            Position = new Vector2(_origin.X + (S(DesignW) - w) * 0.5f, _origin.Y + S(LogoY)),
            Size = new Vector2(w, h),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest, // 像素画 nearest
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.AddChild(logo);
    }

    /// <summary>主题素材统一加载:dll 同目录(与 gd 侧 _mod_dir() 同一处);缺席/损坏返回 null。</summary>
    private static ImageTexture LoadThemeTexture(string file)
    {
        try
        {
            string path = Path.Combine(
                Path.GetDirectoryName(typeof(ItsLoading).Assembly.Location) ?? ".", file);
            if (!File.Exists(path)) return null;
            var img = new Image();
            return img.LoadPngFromBuffer(File.ReadAllBytes(path)) == Error.Ok
                ? ImageTexture.CreateFromImage(img)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Label AddLabel(Vector2 pos, int designFont, Color color)
    {
        var l = new Label
        {
            Position = pos,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("font_size", F(designFont));
        l.AddThemeColorOverride("font_color", color);
        _root.AddChild(l);
        return l;
    }

    /// <summary>2px 白描边空心框 + 内缩 4px 白填充(逐像素等价 Minecraft 风格 nine-slice 条)。</summary>
    private void AddBar(Vector2 pos, out ColorRect fill)
    {
        var sb = new StyleBoxFlat { BorderColor = TextColor, DrawCenter = false };
        sb.SetBorderWidthAll((int)Math.Round(BorderWidth * _scale));
        var outline = new Panel
        {
            Position = pos,
            Size = new Vector2(S(BarWidth), S(BarHeight)),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        outline.AddThemeStyleboxOverride("panel", sb);
        fill = new ColorRect
        {
            Position = new Vector2(S(FillInset), S(FillInset)),
            Size = Vector2.Zero,
            Color = TextColor,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        outline.AddChild(fill);
        _root.AddChild(outline);
        _maxFillW = S(BarWidth - 2f * FillInset);
    }

    private void AddFox()
    {
        try
        {
            // 28 帧竖排精灵:整张做成 atlas,Animator 逐帧换 region
            ImageTexture sheet = LoadThemeTexture("fox_running.png");
            if (sheet == null) return;
            _foxAtlas = new AtlasTexture
            {
                Atlas = sheet,
                Region = new Rect2(0f, 0f, FoxW, FoxH),
            };
            var fox = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, // 同 logo:须先于 Texture,防最小尺寸钳制
                Texture = _foxAtlas,
                Position = P(DesignW - FoxRight - FoxW, DesignH - FoxBottom - FoxH),
                Size = new Vector2(S(FoxW), S(FoxH)),
                StretchMode = TextureRect.StretchModeEnum.Scale,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest, // 像素画 nearest
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _root.AddChild(fox);
        }
        catch (Exception e)
        {
            // 素材缺席只少一只狐狸,绝不让启动 UI 失败
            Log.Warn($"[ItsLoading] fox sprite not loaded ({e.Message}) — theme continues without it");
        }
    }

    private void AddVersionLabel()
    {
        var v = new Label
        {
            Text = $"It's Loading v{typeof(ItsLoading).Assembly.GetName().Version}",
            Position = P(DesignW - VersionRight - 300f, DesignH - VersionBottom - 16f),
            Size = new Vector2(S(300f), S(16f)),
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        v.AddThemeFontSizeOverride("font_size", F(12));
        v.AddThemeColorOverride("font_color", DimTextColor);
        _root.AddChild(v);
    }

    // 设计坐标 → 屏幕:P 平移,S 缩放,F 字号取整
    private Vector2 P(float dx, float dy) => _origin + new Vector2(dx, dy) * _scale;
    private float S(float design) => design * _scale;
    private int F(int designFont) => Math.Max(1, (int)Math.Round(designFont * _scale));

    private static string StageText(LoadingViewState state) => I18n.T("bar.stage", new()
    {
        ["n"] = ((int)state.Stage).ToString(),
        ["t"] = LoadingViewState.StageCount.ToString(),
        ["name"] = state.Step ?? "",
    });

    /// <summary>呈现双条 + 两行文案;不定进度 = 滑块(Animator 持续位移)。</summary>
    public void Present(LoadingViewState state)
    {
        if (!UiOk || _barDead) return;
        ItsLoading.Run("update nf bar", () =>
        {
            _overallFill.Size = new Vector2(_maxFillW * state.Overall, S(BarHeight - 2f * FillInset));
            if (state.LocalIndeterminate)
            {
                _animator.Indeterminate = true;
                _localFill.Size = new Vector2(_maxFillW / 4f, S(BarHeight - 2f * FillInset));
            }
            else
            {
                _animator.Indeterminate = false;
                _localFill.Position = new Vector2(S(FillInset), S(FillInset));
                _localFill.Size = new Vector2(_maxFillW * state.Local, S(BarHeight - 2f * FillInset));
            }
            if (state.Step != null) _stepLabel.Text = StageText(state);
            if (state.Detail != null) _detailLabel.Text = state.Detail;
            LogLine(state);
            if (state.ForceDraw) RenderingServer.ForceDraw();
        });
    }

    // 活动日志推进:gd _log_line 同款语义(连续相同只记一次;计时的裸「+ms」带上步骤名)
    private void LogLine(LoadingViewState state)
    {
        string d = state.Detail;
        string text = string.IsNullOrEmpty(d)
            ? state.Step
            : d.StartsWith('+') ? $"{state.Step} {d}" : d;
        if (string.IsNullOrEmpty(text) || text == _lastLog || _logLabels.Count == 0) return;
        _lastLog = text;
        _logLines.Add(text);
        if (_logLines.Count > LogLines) _logLines.RemoveRange(0, _logLines.Count - LogLines);
        int off = _logLines.Count - _logLabels.Count;
        for (int i = 0; i < _logLabels.Count; i++)
            _logLabels[i].Text = i + off >= 0 ? _logLines[i + off] : "";
    }

    /// <summary>退休:置死亡标志 + 淡出揭幕(有自然帧;结束后整层释放)。</summary>
    public void Retire()
    {
        _barDead = true;
        if (_layer == null) return;
        var tw = _root.CreateTween();
        tw.TweenProperty(_root, "modulate:a", 0f, FadeSeconds);
        tw.TweenCallback(Callable.From(() => _layer.QueueFree()));
    }

    /// <summary>
    /// 自然帧驱动的小动画器:狐狸逐帧换 region + 不定进度滑块位移。
    /// 同步突发期 _Process 不跑 → 两者自动冻结(与 gd _process 同语义)。
    /// </summary>
    private sealed class Animator : Node
    {
        public AtlasTexture Fox;
        public ColorRect LocalFill;
        public bool Indeterminate;
        public float FillBaseX, SlabTravelW;
        public float FoxFrameH, FoxFps;
        public int FoxFrames;
        private float _t;

        public override void _Process(double delta)
        {
            _t += (float)delta;
            if (Fox != null)
                Fox.Region = new Rect2(0f,
                    Mathf.PosMod((int)(_t * FoxFps), FoxFrames) * FoxFrameH,
                    Fox.Region.Size.X, FoxFrameH);
            if (Indeterminate && LocalFill != null && SlabTravelW > 0f)
                LocalFill.Position = new Vector2(
                    FillBaseX + Mathf.PosMod(_t / IndeterminateCycleSeconds, 1f) * SlabTravelW,
                    LocalFill.Position.Y);
        }
    }
}
