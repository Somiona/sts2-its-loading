using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- Slaytheshin 主题(原神风:白底双排徽记 + 剪贴蒙版填充)
//
// 整屏纯白的居中布局(854×480 设计矩形等比缩放):顶部 logo(同 minespire 槽位),
// 中部两排徽记图标就是进度条——第一排 7 枚标记总进度(当前阶段放大 Enlarge 倍:
// 矩形终身不变,pivot=底边中点 + scale,以底边中点为不动点原地放大、底边与整排
// 保持对齐;其余同尺寸同色,放大是唯一标记),
// 第二排 7 枚 85% 尺寸 + 图标间隙的常驻小圆;阶段进度 = PS 剪贴蒙版式深色填充:
// 一层与基图同形的暗调孪生(×FillTint,徽记 50% 灰 → 75% 深灰)只在轨分数段
// [seg_a, seg_b] 内可见,段随 local 从左往右长,图标与小圆遂被逐渐「灌」深——
// 没有任何横条;不定进度 = 1/4 宽滑段在同一蒙版形状里扫过(minespire slab 同款)。
// 底部居中 3 行 × 每行 5 条的灰色活动日志(整行淘汰滚动);进度区不写任何文字。
// 本类是首启/桥版本不匹配时的 C# 兜底;正常路径的同一布局由 gd 模板
// (BootSplash.cs,经 @@SS_*@@ token 与本类常量同源)从帧 0 呈现。
// 仍守经典主题约束:不用 Container、全手动定位;揭幕为淡出(菜单就绪后
// 有自然帧,tween 可靠推进)。

internal sealed class SlaytheshinBar : ILoadingTheme
{
    // ---- Slaytheshin 主题常量(gd 模板经 token 消费,勿在模板写死) ----
    internal static readonly Color BgColor = new(1f, 1f, 1f, 1f);
    internal static readonly Color TextColor = new(0.349f, 0.349f, 0.349f, 1f); // 35% 灰 #595959
    internal static readonly Color VersionColor = new(0.847f, 0.847f, 0.847f, 1f); // 很浅的 15% 灰 #D9D9D9
    internal static readonly Color FillTint = new(0.5f, 0.5f, 0.5f, 1f); // 暗调系数:基图 50% 灰 → 25% 亮度(≈#404040,PS 75% 灰)
    internal static readonly Color DotColor = new(0.502f, 0.502f, 0.502f, 1f); // 常驻小圆基色(50% 灰,与徽记同灰阶)
    internal const float DotScale = 0.2f;    // 小圆直径 = 第二排图标尺寸的比例(1/5)
    internal const int CircleTexSize = 32;   // 生成圆点纹理边长(有效半径 45%,边缘 1px 抗锯齿)
    internal static readonly Color PlaceholderColor = new(0.502f, 0.502f, 0.502f, 1f); // 缺图槽位灰方块

    internal const float DesignW = 854f, DesignH = 480f;
    internal const float LogoY = 56f;        // logo 顶部(minespire 槽位再上移设计高 1/12;缺席回退同位文字标题)
    internal const float LogoDesignW = 520f; // logo 设计宽(高按图比例自适应)
    internal const int FallbackTitleFont = 28;
    internal const int IconsPerRow = 7;      // = BootStage 1..7(行2 同数,子步离散化)
    internal const float Row1IconSize = 44f, Row1Gap = 20f, Row1Cy = 308f; // 行组居中偏下,与 log 块留出空档
    internal const float Enlarge = 1.2f;     // 当前阶段图标放大倍数(底边中点为锚,原地放大)
    internal const float Row2Scale = 0.85f, Row2Gap = 12f, Row2Cy = 366f; // 更小更密
    internal const int LogLines = 3, LogPerLine = 5; // 整行淘汰的活动日志窗口
    internal const string LogSeparator = " | ";      // 条目内已含「·」,故避开
    internal const float LogFont = 8.4f;     // 日志小字(刻意轻量,进度区之外的信息不抢戏)
    internal static readonly Color LogColor = new(0.502f, 0.502f, 0.502f, 1f); // 50% 灰 #808080,与徽记同灰阶
    internal const float LogLineH = 12f, LogBottom = 30f, LogSidePad = 24f;
    internal const float VersionLeft = 24f, VersionTop = 24f; // 左上角,比日志字稍小
    internal const float VersionFont = 7.5f;
    internal const float IndeterminateCycleSeconds = 3f; // 滑块扫满一程(与 gd @@MS_CYCLE_S@@ 同值配对)
    internal const float FadeSeconds = 0.4f;             // 揭幕淡出(与 gd @@MS_FADE_S@@ 同值配对)

    // 剪贴蒙版暗层 shader:贴图按 tint 暗调,仅轨分数段 [seg_a, seg_b] 内可见;
    // nf_* = 本节点在轨上的分数几何(构建期烘焙)。进度更新只写 seg_a/seg_b(全节点
    // 同值)——set_shader_parameter 走 RenderingServer,不触 Control 矩形,同步突发
    // 冻结期照常生效(与第一排放大标记的变换路径同款约束)。
    private const string FillShaderCode = """
        shader_type canvas_item;
        uniform float seg_a = 0.0;
        uniform float seg_b = 0.0;
        uniform float nf_left = 0.0;
        uniform float nf_width = 1.0;
        uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

        void fragment() {
        	vec4 c = texture(TEXTURE, UV);
        	float x = nf_left + UV.x * nf_width;
        	float vis = step(seg_a, x) * step(x, seg_b) * step(0.0001, seg_b - seg_a);
        	COLOR = vec4(c.rgb * tint.rgb, c.a * tint.a * vis);
        }
        """;

    private CanvasLayer _layer;
    private Control _root;      // 全屏根:淡出对它做 modulate(CanvasLayer 无此属性)
    private readonly Control[] _row1 = new Control[IconsPerRow];
    // 放大标记只写 Scale(变换路径),不写 Size:同步突发期引擎不冲洗画布项重绘,
    // Size 写入的重绘请求会被 C# 侧 ForceDraw 吃掉而落屏停滞在旧尺寸(2026-08-30
    // 实机像素级实测:Position 按末阶段生效、drawn size 停在阶段 5)。变换随帧即时
    // 生效,且 normal 尺寸绘制命令 × scale 恒正确——冻结期零重绘也永远不错。
    // 本兜底虽在自然帧下运行,与 gd 模板同款写法保持双侧一致。
    private readonly List<ShaderMaterial> _fillMats = new(2 * IconsPerRow - 1); // 暗色孪生层材质(段参数统一驱动,见 ApplyFill)
    private readonly List<Label> _logLabels = new(LogLines);
    private readonly List<string> _logEntries = new(LogLines * LogPerLine);
    private string _lastLog = "";
    private int _lastStage;     // 换阶段才重排第一排放大标记(不逐帧)
    private Animator _animator; // 不定进度滑块(自然帧驱动,突发期自动冻结)
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
        AddRows();
        AddLogLabels();
        AddVersionLabel();

        _animator = new Animator
        {
            Mats = _fillMats,
            CycleSeconds = IndeterminateCycleSeconds,
        };
        _root.AddChild(_animator);

        // 首次注入提示(版本号下方,不挡布局)
        if (BootSplash.InjectedThisRun)
        {
            var hint = AddLabel(new Vector2(VersionLeft, VersionTop + 16f), 14, TextColor);
            hint.Text = I18n.T("hint.injected");
            var t = tree.CreateTimer(8.0);
            t.Timeout += () => ItsLoading.Run("hide injection hint",
                () => { if (GodotObject.IsInstanceValid(hint)) hint.QueueFree(); });
        }

        // 必须直接 AddChild(同步突发期间 deferred 队列不执行),首帧内容随 BeginMods 呈现
        tree.Root.AddChild(_layer);
        Log.Warn($"[ItsLoading] slaytheshin bar attached (viewport {vs.X}x{vs.Y}, scale {_scale:0.##})");
    }

    /// <summary>原神风 logo(设计宽 520、高按图比例,水平居中,同 minespire 槽位);素材缺席回退文字标题。</summary>
    private void AddLogo()
    {
        ImageTexture tex = LoadThemeTexture("slaytheshin_logo.png");
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
            // 钳制陷阱:默认 KEEP_SIZE 的最小尺寸=贴图尺寸,初始化器按源序赋值,
            // ExpandMode 必须首项(否则后续 Size 赋值被旧 min 钳住)。
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            Texture = tex,
            Position = new Vector2(_origin.X + (S(DesignW) - w) * 0.5f, _origin.Y + S(LogoY)),
            Size = new Vector2(w, h),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.AddChild(logo);
    }

    /// <summary>
    /// 两排徽记 + 剪贴蒙版填充层。z 序:行2 基图 → 基圆 → 暗色孪生层 → 行1 图标
    /// (孪生只属于第二排)。单槽缺图 → 灰方块占位,布局数学不变——主题不因缺图失败。
    /// </summary>
    private void AddRows()
    {
        // 第二排(当前进度):85% 尺寸、更密。剪贴蒙版 = 图标+小圆是蒙版形状,深色
        // 内容(基图 × FillTint)只在轨分数段 [seg_a, seg_b] 内可见,段随 local 从左
        // 往右长 → 图标与小圆被逐渐「灌」深。几何全用轨分数(shader 内映射),进度
        // 更新只写段参数,矩形终身不变(见 AddFillNode 与突发冻结期约束)。
        float s2 = Row1IconSize * Row2Scale;
        float span2 = IconsPerRow * s2 + (IconsPerRow - 1) * Row2Gap;
        float x2 = (DesignW - span2) / 2f;
        float dot = s2 * DotScale;
        ImageTexture circleTex = CircleTexture();
        for (int j = 0; j < IconsPerRow - 1; j++)
        {
            float dcx = x2 + (j + 1) * s2 + j * Row2Gap + Row2Gap / 2f;
            Rect2 drect = CenterRect(dcx, Row2Cy, dot);
            AddTextureRect(circleTex, drect, DotColor); // 常驻浅灰小圆(50%,与徽记同灰阶)
            AddFillNode(circleTex, drect, (dcx - dot / 2f - x2) / span2, dot / span2,
                DotColor * FillTint);
        }
        for (int i = 0; i < IconsPerRow; i++)
        {
            Rect2 rect = CenterRect(x2 + i * (s2 + Row2Gap) + s2 / 2f, Row2Cy, s2);
            AddIcon($"slaytheshin_{IconsPerRow + i + 1}.png", rect);
            // 孪生基色 = 白(白方块占位)或徽记纹理自身灰阶,×FillTint 后同为 75% 深灰档
            AddFillNode(LoadThemeTexture($"slaytheshin_{IconsPerRow + i + 1}.png") ?? WhiteTexture(),
                rect, i * (s2 + Row2Gap) / span2, s2 / span2, FillTint);
        }

        // 第一排(总进度):normal 矩形一次预计算,矩形终身不变——放大是 pivot+scale
        // (见 ApplyStageLayout),底边中点为不动点,底边与整排对齐、原地向上/两侧长大。
        float rowBottom = Row1Cy + Row1IconSize / 2f;
        for (int i = 0; i < IconsPerRow; i++)
        {
            float cx = (DesignW - (IconsPerRow * Row1IconSize + (IconsPerRow - 1) * Row1Gap)) / 2f
                       + i * (Row1IconSize + Row1Gap) + Row1IconSize / 2f;
            _row1[i] = AddIcon($"slaytheshin_{i + 1}.png", BottomCenterRect(cx, rowBottom, Row1IconSize));
        }
        ApplyStageLayout(1); // 工坊期(阶段 1)起即有放大标记
    }

    private Control AddIcon(string file, Rect2 rect)
    {
        ImageTexture tex = LoadThemeTexture(file);
        Control c;
        if (tex != null)
        {
            c = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, // 钳制陷阱:须先于 Texture
                Texture = tex,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
        }
        else
        {
            c = new ColorRect { Color = PlaceholderColor, MouseFilter = Control.MouseFilterEnum.Ignore };
        }
        c.Position = rect.Position;
        c.Size = rect.Size;
        c.PivotOffset = new Vector2(rect.Size.X / 2f, rect.Size.Y); // 底边中点 = Scale 的不动点
        _root.AddChild(c);
        return c;
    }

    /// <summary>带色调制的 TextureRect(基圆等纯色小件;矩形一次写死,后续零写入)。</summary>
    private void AddTextureRect(ImageTexture tex, Rect2 rect, Color color)
    {
        var t = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, // 钳制陷阱:须先于 Texture
            Texture = tex,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Modulate = color,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        t.Position = rect.Position;
        t.Size = rect.Size;
        _root.AddChild(t);
    }

    /// <summary>
    /// 暗色孪生节点:与基图同 rect,shader 按 tint 暗调、仅轨分数段 [seg_a, seg_b] 内
    /// 可见(PS 剪贴蒙版的深色内容)。构建期写死矩形与几何(nf_*),进度只走 shader
    /// 参数(Present/Animator → ApplyFill)——不触 Control 矩形,同步突发冻结期照常
    /// 生效(与第一排放大标记的变换路径同款约束)。
    /// </summary>
    private void AddFillNode(ImageTexture tex, Rect2 rect, float nfLeft, float nfWidth, Color tint)
    {
        var t = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, // 钳制陷阱:须先于 Texture
            Texture = tex,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        t.Position = rect.Position;
        t.Size = rect.Size;
        var mat = new ShaderMaterial();
        mat.Shader = FillShader();
        mat.SetShaderParameter("tint", tint);
        mat.SetShaderParameter("nf_left", nfLeft);
        mat.SetShaderParameter("nf_width", nfWidth);
        mat.SetShaderParameter("seg_a", 0f);
        mat.SetShaderParameter("seg_b", -1f); // 空段:首帧填充前不可见
        t.Material = mat;
        _root.AddChild(t);
        _fillMats.Add(mat);
    }

    /// <summary>生成的小圆纹理(纯白,alpha 边缘 1px 抗锯齿;着色走 modulate / 孪生 tint)。</summary>
    private static ImageTexture CircleTexture()
    {
        float r = CircleTexSize * 0.45f; // 有效半径 45%:留 1px 淡出带
        float c = (CircleTexSize - 1) / 2f;
        Image img = Image.CreateEmpty(CircleTexSize, CircleTexSize, false, Image.Format.Rgba8);
        for (int y = 0; y < CircleTexSize; y++)
        for (int x = 0; x < CircleTexSize; x++)
        {
            float d = new Vector2(x - c, y - c).Length();
            img.SetPixel(x, y, new Color(1f, 1f, 1f, Math.Clamp(r - d + 0.5f, 0f, 1f)));
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>缺图占位槽的孪生用纯白小方块(着色 = PlaceholderColor × FillTint,与基占位同构)。</summary>
    private static ImageTexture WhiteTexture()
    {
        Image img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        img.Fill(Colors.White);
        return ImageTexture.CreateFromImage(img);
    }

    private static Shader _fillShader;

    /// <summary>剪贴蒙版暗层 shader,进程内共享一份 Shader 资源(各材质持自己的参数)。</summary>
    private static Shader FillShader()
    {
        if (_fillShader != null) return _fillShader;
        var s = new Shader();
        s.Code = FillShaderCode;
        _fillShader = s;
        return s;
    }

    /// <summary>段参数直达全部孪生材质(全节点同值;只动 shader 参数,不触矩形)。</summary>
    private void ApplyFill(FillSegment seg)
    {
        foreach (ShaderMaterial m in _fillMats)
        {
            m.SetShaderParameter("seg_a", seg.A);
            m.SetShaderParameter("seg_b", seg.B);
        }
    }

    /// <summary>
    /// 第一排放大标记:当前阶段 Scale=Enlarge、其余 1.0。矩形终身不变(见 _row1 注释)。
    /// 只写变换路径(Position 同款,突发冻结期照常生效),永不触发「改尺寸→等重绘」。
    /// </summary>
    private void ApplyStageLayout(int stage)
    {
        int idx = Math.Clamp(stage, 1, IconsPerRow) - 1;
        for (int i = 0; i < _row1.Length; i++)
        {
            if (_row1[i] == null) continue;
            _row1[i].Scale = i == idx ? Vector2.One * Enlarge : Vector2.One;
        }
    }

    private void AddLogLabels()
    {
        for (int i = 0; i < LogLines; i++)
        {
            var l = AddLabel(P(LogSidePad, DesignH - LogBottom - (LogLines - i) * LogLineH), LogFont, LogColor);
            l.Size = new Vector2(S(DesignW - 2f * LogSidePad), S(LogLineH));
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; // 5 条/行可能超宽,截尾省略
            _logLabels.Add(l);
        }
    }

    private void AddVersionLabel()
    {
        var v = new Label
        {
            Text = $"It's Loading v{typeof(ItsLoading).Assembly.GetName().Version}",
            Position = P(VersionLeft, VersionTop),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        v.AddThemeFontSizeOverride("font_size", F(VersionFont));
        v.AddThemeColorOverride("font_color", VersionColor);
        _root.AddChild(v);
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

    private Label AddLabel(Vector2 pos, float designFont, Color color)
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

    // 设计坐标 → 屏幕:P 平移,S 缩放,F 字号取整,C 中心矩形,BC 底边中点锚定矩形
    private Vector2 P(float dx, float dy) => _origin + new Vector2(dx, dy) * _scale;
    private float S(float design) => design * _scale;
    private int F(float designFont) => Math.Max(1, (int)Math.Round(designFont * _scale));
    private Rect2 CenterRect(float cx, float cy, float size) =>
        new(_origin + new Vector2(cx - size / 2f, cy - size / 2f) * _scale,
            new Vector2(S(size), S(size)));
    private Rect2 BottomCenterRect(float cx, float bottomY, float size) =>
        new(_origin + new Vector2(cx - size / 2f, bottomY - size) * _scale,
            new Vector2(S(size), S(size)));

    /// <summary>呈现放大标记 + 剪贴蒙版填充;不定进度 = 滑段(Animator 持续位移)。进度区不写文字。</summary>
    public void Present(LoadingViewState state)
    {
        if (!UiOk || _barDead) return;
        ItsLoading.Run("update ss bar", () =>
        {
            if ((int)state.Stage != _lastStage)
            {
                _lastStage = (int)state.Stage;
                ApplyStageLayout(_lastStage);
            }
            if (state.LocalIndeterminate)
            {
                _animator.Indeterminate = true; // 段位置由 Animator 自然帧驱动(与旧滑块分工同款)
            }
            else
            {
                _animator.Indeterminate = false;
                ApplyFill(SlaytheshinFill.Segment(state.Local, false, 0f, 1f));
            }
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
        SlaytheshinLog.Append(_logEntries, text);
        string[] lines = SlaytheshinLog.Render(_logEntries);
        for (int i = 0; i < _logLabels.Count; i++) _logLabels[i].Text = lines[i];
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
    /// 自然帧驱动的小动画器:不定进度滑段位移(公式在 SlaytheshinFill.Segment)。
    /// 同步突发期 _Process 不跑 → 自动冻结(与 gd _process 同语义)。
    /// </summary>
    private sealed class Animator : Node
    {
        public List<ShaderMaterial> Mats = new();
        public bool Indeterminate;
        public float CycleSeconds;
        private float _t;

        public override void _Process(double delta)
        {
            if (!Indeterminate || Mats.Count == 0 || CycleSeconds <= 0f) return;
            _t += (float)delta;
            FillSegment seg = SlaytheshinFill.Segment(0f, true, _t, CycleSeconds);
            foreach (ShaderMaterial m in Mats)
            {
                m.SetShaderParameter("seg_a", seg.A);
                m.SetShaderParameter("seg_b", seg.B);
            }
        }
    }
}

/// <summary>
/// 第二排填充段(纯函数,gd 模板 _ss_sync_fill 同款公式):确定进度 = 段 [0, local];
/// 不定进度 = 宽 SweepWidth 的滑段在轨上循环扫(头部 PosMod(t/cycle) × SweepTravel,
/// 右缘恰好可达轨满宽)。Godot 类型零依赖(PosMod 手写、只用 System.Math),
/// 测试工程无需引擎程序集。
/// </summary>
internal static class SlaytheshinFill
{
    internal const float SweepWidth = 0.25f;  // 滑段宽(轨的 1/4,与 minespire slab 同语义)
    internal const float SweepTravel = 0.75f; // 滑段头部行程(左缘 0 → 0.75)

    internal static FillSegment Segment(float local, bool indeterminate, float t, float cycleSeconds)
    {
        if (!indeterminate)
            return new FillSegment(0f, Math.Clamp(local, 0f, 1f));
        float head = t / cycleSeconds;
        head -= MathF.Floor(head); // 周期回绕
        float a = head * SweepTravel;
        return new FillSegment(a, a + SweepWidth);
    }
}

/// <summary>轨分数段(剪贴蒙版可见区间;A ≥ B = 空段 = 不可见)。</summary>
internal readonly record struct FillSegment(float A, float B);

/// <summary>
/// 底部活动日志窗口(纯函数,gd 模板 _ss_log_render 同款算法):
/// 扁平条目表 + 分块渲染 = 行粒度滚动——每行 LogPerLine 条,窗口 LogLines 行,
/// 超出上限整行淘汰(最旧一行消失,其余整体上移),最新永远在最后一行。
/// </summary>
internal static class SlaytheshinLog
{
    /// <summary>追加一条并维持窗口上限(整行淘汰)。</summary>
    internal static void Append(List<string> entries, string text)
    {
        entries.Add(text);
        if (entries.Count > SlaytheshinBar.LogLines * SlaytheshinBar.LogPerLine)
            entries.RemoveRange(0, SlaytheshinBar.LogPerLine);
    }

    /// <summary>渲染 LogLines 行文本(第 i 行 = 条目 [i×PerLine, …);不足整行取部分,无行给空串)。</summary>
    internal static string[] Render(List<string> entries)
    {
        var lines = new string[SlaytheshinBar.LogLines];
        for (int i = 0; i < lines.Length; i++)
        {
            int start = i * SlaytheshinBar.LogPerLine;
            if (start >= entries.Count)
            {
                lines[i] = "";
                continue;
            }
            int take = Math.Min(SlaytheshinBar.LogPerLine, entries.Count - start);
            lines[i] = string.Join(SlaytheshinBar.LogSeparator, entries.GetRange(start, take));
        }
        return lines;
    }
}
