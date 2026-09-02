using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

/// <summary>
/// 冻结窗口的原生呈现面(macOS 适配):按 ThemeDef(与 gd interpreter 同一份
/// theme.json)在游戏 CAMetalLayer 下挂 CALayer 子树渲染完整主题,绕过 Godot
/// 渲染管线由 Core Animation 渲染服务器直接合成 —— 冻结期(Metal swapchain
/// acquire 静默失败、blit 全跳过)也能更新,帧恢复后继续拥有像素直到 retire。
///
/// 依据(2026-09-01/02 实证,见 M14_METAL_FREEZE_ADDENDUM.md):
///   · beta 冻结 = 主线程卡在 Main::start,runloop 不转 —— 显式 CATransaction
///     正是 Apple 对「runloop 阻塞」场景的文档化姿势,主线程改 layer 树合法;
///   · Godot 侧呈现在启动突发期全部不上屏(合成器留存 boot logo 帧)——
///     原生面是冻结期唯一可见通道,故帧恢复后不退场(活跃模式)。
/// ThemeDef 缺失/构建异常 → 细条兜底(最小诊断呈现面)。窗口 resize
/// (logo-play)经边界监视触发整体重排。诊断开关 ITSLOADING_PROBE_LAYER=1。
/// 坐标:CALayer 底左原点 ↔ Godot 顶左原点,y 翻转统一在 MapY/MapRel。
/// 诚实近似(与 gd 渲染的差异):CATextLayer 系统字体 ≠ Godot 字体;
/// mask 副本暗化 = 半透明黑底叠加(灰阶素材下 ≈ 乘法)。
/// </summary>
internal sealed class MacLayerSurface : IThemeSurface
{
    private readonly ThemeDef? _def;
    private readonly string _themeDir;
    private readonly Func<string, string> _txt;
    private readonly string _version;
    private readonly bool _diag;

    private IntPtr _root;          // 所有元素层的唯一容器(淡出/硬拆的整体句柄)
    private IntPtr _layer;         // 游戏 CAMetalLayer
    private double _bw, _bh;       // 有效空间边界(resize 监视)
    private bool _built, _thin;

    // 空间映射:design → 等比缩放居中;screen → 恒等
    private double _scale = 1, _ox, _oy;
    private double _overlayTop;                // 层顶部被标题栏/菜单栏覆盖的带高

    // 元素运行时
    private readonly List<IntPtr> _statics = new();
    private readonly List<TextNode> _labels = new();
    private readonly List<BarNode> _bars = new();
    private readonly List<RowNode> _rows = new();
    private MaskNode? _mask;
    private readonly List<SpriteNode> _sprites = new();
    private readonly List<LogNode> _logs = new();
    private readonly Dictionary<string, IntPtr> _strips = new();
    private readonly Dictionary<string, IntPtr> _images = new();   // src → CGImage(0 = 缺)

    // 细条兜底
    private IntPtr _thinFill, _thinText;
    private double _thinTrackW;

    private sealed class TextNode
    {
        public IntPtr Layer;
        public ThemeBind Bind;
        public string Last = "";
    }

    private sealed class BarNode
    {
        public IntPtr Track, Fill;
        public ThemeBind Bind;
        public double TrackW, TrackH, Inset, FillY, FillH;
        public IndeterminateDef? Ind;
    }

    private sealed class RowNode
    {
        public string Id = "";
        public IntPtr[] Slots = Array.Empty<IntPtr>();
        public double Size, Cx, Bottom;
        public double Factor = 1;
    }

    private sealed class MaskNode
    {
        public IntPtr MaskLayer;
        public double SpanS, HeightS;
        public IndeterminateDef Ind = new();
    }

    private sealed class SpriteNode
    {
        public IntPtr Layer;
        public double SheetW, SheetH, FrameW, FrameH;
        public int Frames;
        public double Fps;
    }

    private sealed class LogNode
    {
        public IntPtr[] Lines = Array.Empty<IntPtr>();
        public int PerLine = 1;
        public bool Column = true;
        public string Sep = "";
        public int LastCount = -1;
        public string LastTail = "";
    }

    private readonly bool _calib;

    internal MacLayerSurface(ThemeDef? def, string themeDir, string version,
        Func<string, string>? txt = null, bool calib = false)
    {
        _def = def;
        _themeDir = themeDir;
        _version = version;
        _txt = txt ?? (k => k);
        _calib = calib;
        _diag = System.Environment.GetEnvironmentVariable("ITSLOADING_PROBE_LAYER") == "1";
    }

    // ================================================================ 生命周期

    public bool TryAttach()
    {
        _layer = GameLayer();
        if (_layer == IntPtr.Zero)
        {
            Log.Warn("[ItsLoading][overlay] game layer not found — inert this boot");
            return false;
        }
        (double ew, double eh, double cs) = EffectiveBounds();
        if (ew < 50 || eh < 50)
        {
            Log.Warn($"[ItsLoading][overlay] implausible layer bounds {ew:F0}x{eh:F0} — inert");
            return false;
        }
        try
        {
            Build(ew, eh);
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading][overlay] themed build failed ({e.Message}) — thin-bar fallback");
            TeardownLayers();
            try { BuildThinBar(ew, eh); }
            catch (Exception e2)
            {
                Log.Warn($"[ItsLoading][overlay] fallback failed too ({e2.Message}) — inert");
                return false;
            }
        }
        _built = true;
        if (_diag) LogGeometryDiag("@attach");
        if (_thin)
            Log.Warn($"[ItsLoading][overlay] thin bar over frozen frame (no theme def) {ew:F0}x{eh:F0}");
        else if (_diag)
        {
            Log.Warn("[ItsLoading][overlay][diag] attached themed " +
                     $"space={ew:F0}x{eh:F0} contentsScale={cs:F1} " +
                     $"designScale={_scale:F2} origin=({_ox:F0},{_oy:F0})");
        }
        return true;
    }

    /// <summary>
    /// 子层坐标系的有效空间 = layer.bounds(Apple 文档语义;点空间)。
    /// 2026-09-02 实证纠结记录:Steam 客户端启动时 bounds 语义正确;本机
    /// 直启(steam_appid 直跑)时子层几何实测按 drawable 像素空间走(同读数
    /// 不同空间,机理未明 —— 见 MacOS_sts2beta_reverse.md)。以真实用户
    /// 环境(Steam)为准;直启仅是开发便利,错位不影响用户。
    /// </summary>
    private (double w, double h, double cs) EffectiveBounds()
    {
        CGRect b = ObjcRect(_layer, Sel("bounds"));
        double cs = ObjcDouble(_layer, Sel("contentsScale"));
        return (b.Width, b.Height, cs);
    }

    // drawableSize 只对 CAMetalLayer 家族存在;无守卫调用 = SIGABRT(实证)

    public void Present(SurfaceView view)
    {
        if (!_built) return;
        // 边界监视:logo-play 的窗口 resize → 整体重排。全屏过渡时游戏可能
        // 重建窗口内容层 —— 缓存的 _layer 会变孤儿(重建的新根挂上去不可见,
        // 旧根又已拆除,屏幕停留在过渡瞬间的画面)。重排前重新解析层引用,
        // 指针不同即换挂(相同则无操作),并留指针证据。
        (double ew, double eh, _) = EffectiveBounds();
        if (Math.Abs(ew - _bw) > 0.5 || Math.Abs(eh - _bh) > 0.5)
        {
            IntPtr fresh = GameLayer();
            bool swapped = fresh != IntPtr.Zero && fresh != _layer;
            if (swapped)
            {
                Log.Warn($"[ItsLoading][overlay] game layer swapped {_layer.ToString("x")} → " +
                         $"{fresh.ToString("x")} — re-attaching");
                _layer = fresh;
            }
            if (_diag)
            {
                bool attached = ObjcId(_root, Sel("superlayer")) != IntPtr.Zero;
                Log.Warn($"[ItsLoading][overlay][diag] relayout {_bw:F0}x{_bh:F0} → " +
                         $"{ew:F0}x{eh:F0} rootAttached={attached} layerSwapped={swapped}");
            }
            TeardownLayers();
            Build(ew, eh);
            if (_diag) LogGeometryDiag("@relayout");
            if (_diag)
            {
                bool live = ObjcId(_root, Sel("superlayer")) == _layer;
                Log.Warn($"[ItsLoading][overlay][diag] rebuilt rootOnTargetLayer={live}");
            }
        }
        if (_thin)
        {
            PresentThin(view);
            return;
        }
        Transaction(() =>
        {
            UpdateBars(view);
            UpdateLabels(view);
            if (view.Snap.StageChanged) UpdateRows(view);
            UpdateMask(view);
            UpdateLogs(view);
            UpdateSprites(view);
        });
    }


    public void Remove(string why)
    {
        // 两段式退场:Remove 只是标记;淡出经 SetOpacity 步进,Teardown 才硬拆
    }

    public void SetOpacity(double opacity)
    {
        if (!_built || _root == IntPtr.Zero) return;
        Transaction(() => ObjcVoidD(_root, Sel("setOpacity:"), Math.Clamp(opacity, 0.0, 1.0)));
    }

    public void Teardown()
    {
        TeardownLayers();
        _built = false;
    }

    private void TeardownLayers()
    {
        if (_root != IntPtr.Zero)
        {
            Transaction(() => ObjcVoid(_root, Sel("removeFromSuperlayer")));
            _root = IntPtr.Zero;
        }
        _statics.Clear();
        _labels.Clear();
        _bars.Clear();
        _rows.Clear();
        _sprites.Clear();
        _logs.Clear();
        _strips.Clear();
        _mask = null;
        _thin = false;
    }

    // ================================================================ 构建

    private void Build(double bw, double bh)
    {
        _bw = bw;
        _bh = bh;
        _thin = false;
        _root = NewLayer();
        Transaction(() => SetFrame(_root, Sel("setFrame:"), new CGRect(0, 0, bw, bh)));
        ObjcLayer(_layer, Sel("addSublayer:"), _root);

        if (_def == null)
        {
            BuildThinBar(bw, bh);
            if (_calib)
                Transaction(() =>
                {
                    ObjcVoidColor(_root, Sel("setBorderColor:"), Color(1, 0, 1, 0.9));
                    ObjcVoidD(_root, Sel("setBorderWidth:"), 6);
                });
            return;
        }
        var space = _def.Space;
        // (2026-09-02 两次证伪记录:① 子层空间按 drawable → Steam 上下文错误;
        // ② 标题栏覆盖带 → 只会整体缩小,真实病灶是「逐元素不同量的垂直错位」。
        // 保留:bounds 空间 + 等比居中。标定网格见 BuildGrid。)
        if (space.IsDesign)
        {
            _scale = Math.Min(bw / space.W, bh / space.H);
            _ox = (bw - space.W * _scale) / 2;
            _oy = (bh - space.H * _scale) / 2;
        }
        else
        {
            _scale = 1;
            _ox = _oy = 0;
        }
        foreach (var e in _def.Elements)
        {
            try { BuildElement(e); }
            catch (Exception ex)
            {
                Log.Warn($"[ItsLoading][overlay] element '{e.Id}' skipped: {ex.Message}");
            }
        }
        if (_calib && _def.Space.IsDesign)
        {
            BuildGrid(_def.Space);
            BuildCalibBoxes(_def);
        }
    }

    /// <summary>
    /// (Debug)开发者标定视图(设置开关,默认关):画布品红边框 + 设计画布
    /// 10% 网格 + 逐元素品红框。框来自 theme.json 声明值,估计规则与 gd 侧
    /// interpreter 的 _calib_rect 逐条对齐 —— 双渲染器截图可程序化比对布局
    /// (比对脚本模式见 2026-09-02 标定会话)。
    /// </summary>
    private void BuildGrid(ThemeSpaceDef space)
    {
        Transaction(() =>
        {
            ObjcVoidColor(_root, Sel("setBorderColor:"), Color(1, 0, 1, 0.9));
            ObjcVoidD(_root, Sel("setBorderWidth:"), 6);
        });
        for (int i = 1; i < 10; i++)
        {
            double t = i / 10.0;
            bool strong = i == 5;
            double w = strong ? 4 : 2;
            var v = NewLayer();
            var h = NewLayer();
            Transaction(() =>
            {
                SetFrame(v, Sel("setFrame:"), new CGRect(_ox + Sc(space.W * t), 0, w, _bh));
                ObjcVoidColor(v, Sel("setBackgroundColor:"), Color(1, 1, 1, strong ? 0.7 : 0.3));
                SetFrame(h, Sel("setFrame:"), new CGRect(0, MapY(space.H * t, 0), _bw, w));
                ObjcVoidColor(h, Sel("setBackgroundColor:"), Color(1, 1, 1, strong ? 0.7 : 0.3));
            });
            ObjcLayer(_root, Sel("addSublayer:"), v);
            ObjcLayer(_root, Sel("addSublayer:"), h);
        }
    }


    private void BuildCalibBoxes(ThemeDef def)
    {
        foreach (var b in CalibRules.Boxes(def))
        {
            var boxLayer = NewLayer();
            Transaction(() =>
            {
                SetFrame(boxLayer, Sel("setFrame:"), new CGRect(
                    _ox + Sc(b.X), MapY(b.Y, b.H), Sc(b.W), Sc(b.H)));
                ObjcVoidColor(boxLayer, Sel("setBorderColor:"), Color(1, 0, 1, 0.85));
                ObjcVoidD(boxLayer, Sel("setBorderWidth:"), 2);
            });
            ObjcLayer(_root, Sel("addSublayer:"), boxLayer);
            _statics.Add(boxLayer);
        }
    }



    /// <summary>诊断时钟(2026-09-02:冻结期提交链路的可见证据 —— 时钟走 = commit
    /// 直达渲染服务器;卡住 = Steam 上下文吞掉中途 commit。定位后移除)。</summary>

    private IntPtr ParentOf(ThemeElementDef e) =>
        e.Parent != null && _strips.TryGetValue(e.Parent, out var s) ? s : _root;

    private void BuildElement(ThemeElementDef e)
    {
        IntPtr parent = ParentOf(e);
        switch (e)
        {
            case BgElement bg:
            {
                IntPtr l = NewLayer();
                Transaction(() =>
                {
                    SetFrame(l, Sel("setFrame:"), new CGRect(0, 0, _bw, _bh));
                    ObjcVoidColor(l, Sel("setBackgroundColor:"), Color(bg.Color));
                });
                ObjcLayer(_root, Sel("addSublayer:"), l);
                _statics.Add(l);
                break;
            }
            case StripElement strip:
            {
                IntPtr l = NewLayer();
                double h = Sc(strip.H);
                Transaction(() => SetFrame(l, Sel("setFrame:"), new CGRect(0, 0, _bw, h)));
                ObjcLayer(_root, Sel("addSublayer:"), l);
                _strips[e.Id] = l;
                break;
            }
            case LogoElement logo:
            {
                IntPtr img = LoadImage(logo.Src);
                if (img != IntPtr.Zero)
                {
                    double iw = (double)CGImageGetWidth(img), ih = (double)CGImageGetHeight(img);
                    double hTheme = iw > 0 ? logo.W * ih / iw : logo.W; // 主题单位
                    AddImageLayer(_root, img, _ox + Sc(logo.X), MapY(logo.Y, hTheme),
                        Sc(logo.W), Sc(hTheme), logo.Nearest);
                }
                else
                {
                    double hTheme = logo.FallbackFont + 6;
                    AddTextLayer(_root, logo.FallbackText, _ox + Sc(logo.X), MapY(logo.Y, hTheme),
                        Sc(logo.W), Sc(hTheme), logo.FallbackFont, logo.FallbackColor, null);
                }
                break;
            }
            case LabelElement l:
            {
                string text = l.Text.Resolve(_txt);
                double hTheme = l.H ?? l.Font * 1.4 + 6;
                double wTheme = l.W ?? 0; // 0 → 文本层自适应宽(下面取画布宽)
                CGRect r = MapRel(parent, l.X, l.Y, wTheme, hTheme);
                IntPtr tl = AddTextLayer(parent, text, r.X, r.Y,
                    l.W.HasValue ? r.Width : _bw, r.Height, l.Font, l.Color, l.Align);
                _labels.Add(new TextNode { Layer = tl, Bind = l.Bind, Last = text });
                break;
            }
            case VersionLabelElement v:
            {
                double hTheme = v.H ?? v.Font * 1.4 + 6;
                CGRect r = MapRel(parent, v.X, v.Y, v.W ?? 0, hTheme);
                AddTextLayer(parent, v.Prefix + _version, r.X, r.Y,
                    v.W.HasValue ? r.Width : _bw, r.Height, v.Font, v.Color, v.Align);
                break;
            }
            case BarSolidElement bar: BuildBar(parent, bar, bar.Track, 0, solid: true); break;
            case BarOutlineElement bar:
                BuildBar(parent, bar, bar.Border, bar.BorderW, solid: false);
                break;
            case IconRowElement row:
            {
                var node = new RowNode
                {
                    Id = row.Id,
                    Size = row.Size,
                    Cx = row.Cx,
                    Bottom = row.Bottom ?? (row.Cy ?? 0) + row.Size / 2,
                    Factor = row.Enlarge?.Factor ?? 1,
                    Slots = new IntPtr[row.Count],
                };
                for (int i = 0; i < row.Count; i++) node.Slots[i] = IntPtr.Zero;
                double span = row.Count * row.Size + (row.Count - 1) * row.Gap;
                double x0 = row.Cx - span / 2;
                for (int i = 0; i < row.Count; i++)
                {
                    string src = row.Src ?? string.Format(row.Pattern ?? "%d", row.IndexBase + i);
                    IntPtr img = LoadImage(src);
                    double topTheme = node.Bottom - row.Size;
                    double lx = _ox + Sc(x0 + i * (row.Size + row.Gap));
                    if (img != IntPtr.Zero)
                        node.Slots[i] = AddImageLayer(_root, img, lx, MapY(topTheme, row.Size),
                            Sc(row.Size), Sc(row.Size), row.Nearest);
                    else
                        node.Slots[i] = AddColorLayer(_root, lx, MapY(topTheme, row.Size),
                            Sc(row.Size), Sc(row.Size),
                            row.Placeholder ?? new ThemeColor(0.5, 0.5, 0.5, 1));
                }
                if (row.Enlarge != null) MarkRowStage(node, 1);
                _rows.Add(node);
                break;
            }
            case DotsElement dots:
            {
                var row = FindRowDef(dots.Of);
                if (row == null) break;
                double sizeTheme = row.Size * dots.Scale;
                double size = Sc(sizeTheme);
                double span = row.Count * row.Size + (row.Count - 1) * row.Gap;
                double x0 = row.Cx - span / 2;
                for (int j = 0; j < row.Count - 1; j++)
                {
                    double cx = x0 + (j + 1) * row.Size + j * row.Gap + row.Gap / 2;
                    double lx = _ox + Sc(cx) - size / 2;
                    var l = NewLayer();
                    Transaction(() =>
                    {
                        SetFrame(l, Sel("setFrame:"), new CGRect(lx, MapY(dots.Cy, sizeTheme), size, size));
                        ObjcVoidD(l, Sel("setCornerRadius:"), size / 2);
                        ObjcVoidColor(l, Sel("setBackgroundColor:"), Color(dots.Color));
                    });
                    ObjcLayer(_root, Sel("addSublayer:"), l);
                }
                break;
            }
            case MaskTrackElement mask:
            {
                IconRowElement? domain = FirstRowMember(mask);
                if (domain == null) break;
                double spanS = Sc(domain.Count * domain.Size + (domain.Count - 1) * domain.Gap);
                double topTheme = (domain.Bottom ?? 0) - domain.Size;
                double contX = _ox + Sc(domain.Cx) - spanS / 2;
                double contH = Sc(domain.Size);
                double contY = MapY(topTheme, domain.Size);

                var container = NewLayer();
                var maskLayer = NewLayer();
                Transaction(() =>
                {
                    SetFrame(container, Sel("setFrame:"), new CGRect(contX, contY, spanS, contH));
                    ObjcVoidB(container, Sel("setMasksToBounds:"), true);
                    SetFrame(maskLayer, Sel("setFrame:"), new CGRect(0, 0, 0, contH));
                    ObjcVoidColor(maskLayer, Sel("setBackgroundColor:"),
                        Color(1, 1, 1, 1));
                    ObjcIdPtr(container, Sel("setMask:"), maskLayer);
                });
                ObjcLayer(_root, Sel("addSublayer:"), container);

                // 暗色副本:icon 成员 = 贴图 + 半透明黑底(≈乘法暗化);
                // dots 成员 = 深灰圆(dot 色 × tint)
                foreach (var memberId in mask.Members)
                {
                    if (_def.Elements.FirstOrDefault(x => x.Id == memberId) is IconRowElement ir)
                    {
                        for (int i = 0; i < ir.Count; i++)
                        {
                            string src = ir.Src ?? string.Format(ir.Pattern ?? "%d", ir.IndexBase + i);
                            IntPtr img = LoadImage(src);
                            double bx = _ox + Sc(RowX0(ir) + i * (ir.Size + ir.Gap)) - contX;
                            if (img != IntPtr.Zero)
                                AddDarkImageCopy(container, img, bx, 0, Sc(ir.Size), Sc(ir.Size), mask.Tint);
                            else
                                AddColorLayer(container, bx, 0, Sc(ir.Size), Sc(ir.Size),
                                    Half(mask.Tint));
                        }
                    }
                    else if (_def.Elements.FirstOrDefault(x => x.Id == memberId) is DotsElement dd
                        && FindRowDef(dd.Of) is { } dr)
                    {
                        double dsize = Sc(dr.Size * dd.Scale);
                        double dspan = dr.Count * dr.Size + (dr.Count - 1) * dr.Gap;
                        double dx0 = dr.Cx - dspan / 2;
                        for (int j = 0; j < dr.Count - 1; j++)
                        {
                            double cx = dx0 + (j + 1) * dr.Size + j * dr.Gap + dr.Gap / 2;
                            double lx = _ox + Sc(cx) - dsize / 2 - contX;
                            double ly = (contH - dsize) / 2;
                            var l = NewLayer();
                            Transaction(() =>
                            {
                                SetFrame(l, Sel("setFrame:"), new CGRect(lx, ly, dsize, dsize));
                                ObjcVoidD(l, Sel("setCornerRadius:"), dsize / 2);
                                ObjcVoidColor(l, Sel("setBackgroundColor:"), Color(Mul(dd.Color, mask.Tint)));
                            });
                            ObjcLayer(container, Sel("addSublayer:"), l);
                        }
                    }
                }
                _mask = new MaskNode { MaskLayer = maskLayer, SpanS = spanS, HeightS = contH,
                    Ind = mask.Indeterminate };
                break;
            }
            case SpriteElement spr:
            {
                IntPtr img = LoadImage(spr.Src);
                if (img == IntPtr.Zero) break; // 缺素材整元素跳过(与 gd 同语义)
                IntPtr l = AddImageLayer(_root, img, _ox + Sc(spr.X), MapY(spr.Y, spr.H),
                    Sc(spr.W), Sc(spr.H), spr.Nearest);
                _sprites.Add(new SpriteNode
                {
                    Layer = l,
                    SheetW = (double)CGImageGetWidth(img), SheetH = (double)CGImageGetHeight(img),
                    FrameW = spr.FrameW, FrameH = spr.FrameH, Frames = spr.Frames, Fps = spr.Fps,
                });
                break;
            }
            case LogColumnElement log: BuildLog(log, parent, column: true); break;
            case LogRowsElement log: BuildLog(log, parent, column: false); break;
        }
    }

    private void BuildBar(IntPtr parent, BarElementDef bar, ThemeColor edge, double edgeW, bool solid)
    {
        double insetTheme = solid ? 0 : ((BarOutlineElement)bar).Inset;
        CGRect r = MapRel(parent, bar.X, bar.Y,
            bar.W.IsFill ? double.NaN : bar.W.Value, bar.H);
        if (bar.W.IsFill)
        {
            // fill 宽 = 所在空间宽 − 2x(strip 子元素按父宽,根下按画布宽)
            double spaceW = parent == _root ? _bw : ObjcRect(parent, Sel("frame")).Width;
            r = new CGRect(r.X, r.Y, spaceW - 2 * Sc(bar.X), r.Height);
        }
        double inset = Sc(insetTheme);
        var track = NewLayer();
        var fill = NewLayer();
        Transaction(() =>
        {
            SetFrame(track, Sel("setFrame:"), r);
            if (solid) ObjcVoidColor(track, Sel("setBackgroundColor:"), Color(((BarSolidElement)bar).Track));
            else
            {
                ObjcVoidColor(track, Sel("setBorderColor:"), Color(edge));
                ObjcVoidD(track, Sel("setBorderWidth:"), Sc(edgeW));
            }
            SetFrame(fill, Sel("setFrame:"), new CGRect(inset, inset, 1, r.Height - 2 * inset));
            ObjcVoidColor(fill, Sel("setBackgroundColor:"), Color(bar.Fill));
        });
        ObjcLayer(parent, Sel("addSublayer:"), track);
        ObjcLayer(track, Sel("addSublayer:"), fill);
        _bars.Add(new BarNode
        {
            Track = track, Fill = fill, Bind = bar.Bind,
            TrackW = r.Width - 2 * inset, TrackH = r.Height, Inset = inset,
            FillY = inset, FillH = r.Height - 2 * inset, Ind = bar.Indeterminate,
        });
    }

    private void BuildLog(LogElementDef log, IntPtr parent, bool column)
    {
        var node = new LogNode { Column = column, PerLine = column ? 1 : ((LogRowsElement)log).PerLine,
            Sep = column ? "" : ((LogRowsElement)log).Sep, Lines = new IntPtr[log.Lines] };
        for (int i = 0; i < log.Lines; i++)
        {
            double wTheme = column || ((LogRowsElement)log).W == 0 ? 0 : ((LogRowsElement)log).W;
            CGRect r = MapRel(parent, log.X, log.Y + i * log.LineH, wTheme, log.LineH);
            var c = log.Color;
            ThemeColor col = column ? new ThemeColor(c.R, c.G, c.B, ColumnAlpha(i, log.Lines, c.A)) : c;
            node.Lines[i] = AddTextLayer(parent, "", r.X, r.Y,
                wTheme == 0 ? _bw : r.Width, r.Height, log.Font, col, log.Align);
        }
        _logs.Add(node);
    }

    private static double RowX0(IconRowElement r) =>
        r.Cx - (r.Count * r.Size + (r.Count - 1) * r.Gap) / 2;

    private IconRowElement? FindRowDef(string id) =>
        _def?.Elements.FirstOrDefault(x => x.Id == id) as IconRowElement;

    private IconRowElement? FirstRowMember(MaskTrackElement m)
    {
        foreach (var id in m.Members)
            if (FindRowDef(id) is { } r) return r;
        return null;
    }

    // ================================================================ 更新

    private void UpdateBars(SurfaceView v)
    {
        foreach (var b in _bars)
        {
            double frac = b.Bind == ThemeBind.Overall ? v.Snap.Overall : v.Snap.Local;
            if (double.IsNaN(frac)) frac = 0;
            double x = b.Inset, w;
            if (b.Bind == ThemeBind.Local && v.Snap.LocalIndeterminate && b.Ind != null)
            {
                if (b.Ind.Mode == IndeterminateMode.Pulse)
                {
                    // classic shimmer:宽度呼吸(三角波),与 kit.pulse_width 同式
                    double tri = Math.Abs((v.Snap.T * 0.8) % 2.0 - 1.0);
                    w = Math.Clamp(Sc(b.Ind.MinW) + tri * Sc(b.Ind.Travel), 0, b.TrackW);
                }
                else
                {
                    double phase = (v.Snap.T / Math.Max(0.1, b.Ind.CycleS)) % 1.0;
                    w = b.TrackW * 0.25;
                    x = b.Inset + phase * b.TrackW * 0.75;
                }
            }
            else
            {
                w = Math.Clamp(frac, 0, 1) * b.TrackW;
            }
            SetFrame(b.Fill, Sel("setFrame:"), new CGRect(x, b.FillY, Math.Max(1, w), b.FillH));
        }
    }

    private void UpdateLabels(SurfaceView v)
    {
        foreach (var t in _labels)
        {
            string text = t.Bind switch
            {
                ThemeBind.Step => v.Snap.StepText,
                ThemeBind.Detail => v.Snap.DetailText,
                _ => t.Last,
            };
            if (text == t.Last) continue;
            t.Last = text;
            IntPtr cf = CfString(text);
            ObjcIdPtr(t.Layer, Sel("setString:"), cf);
            CfRelease(cf);
        }
    }

    private void UpdateRows(SurfaceView v)
    {
        foreach (var r in _rows)
            if (r.Factor != 1) MarkRowStage(r, (int)v.State.Stage);
    }

    /// <summary>当前阶段放大:底中点锚的放大矩形(CALayer 直接 setFrame,
    /// 无 Godot 侧「改尺寸滞留」约束)。</summary>
    private void MarkRowStage(RowNode r, int stage)
    {
        var def = FindRowDef(r.Id);
        if (def == null) return;
        int idx = Math.Clamp(stage, 1, r.Slots.Length) - 1;
        double bottomTheme = def.Bottom ?? (def.Cy ?? 0) + def.Size / 2;
        for (int i = 0; i < r.Slots.Length; i++)
        {
            double size = Sc(def.Size) * (i == idx ? r.Factor : 1);
            double cx = _ox + Sc(def.Cx);
            double bottomY = _bh - _oy - Sc(bottomTheme);
            SetFrame(r.Slots[i], Sel("setFrame:"), new CGRect(cx - size / 2, bottomY, size, size));
        }
    }

    private void UpdateMask(SurfaceView v)
    {
        var m = _mask;
        if (m == null) return;
        double a, b;
        if (v.Snap.LocalIndeterminate)
        {
            a = ((v.Snap.T / Math.Max(0.1, m.Ind.CycleS)) % 1.0) * 0.75;
            b = a + 0.25;
        }
        else
        {
            double local = double.IsNaN(v.Snap.Local) ? 0 : v.Snap.Local;
            a = 0;
            b = Math.Clamp(local, 0, 1);
        }
        double w = Math.Max(0, (b - a) * m.SpanS);
        SetFrame(m.MaskLayer, Sel("setFrame:"), new CGRect(a * m.SpanS, 0, w, m.HeightS));
    }

    private void UpdateLogs(SurfaceView v)
    {
        var entries = v.Snap.Log;
        foreach (var node in _logs)
        {
            if (entries.Count == node.LastCount && (entries.Count == 0 || entries[^1] == node.LastTail))
                continue;
            node.LastCount = entries.Count;
            node.LastTail = entries.Count > 0 ? entries[^1] : "";
            for (int i = 0; i < node.Lines.Length; i++)
            {
                string text = node.Column
                    ? LogColumnLine(entries, i, node.Lines.Length)
                    : LogRowsLine(entries, i, node);
                IntPtr cf = CfString(text);
                ObjcIdPtr(node.Lines[i], Sel("setString:"), cf);
                CfRelease(cf);
            }
        }
    }

    private static string LogColumnLine(IReadOnlyList<string> entries, int i, int lines)
    {
        int off = entries.Count - lines;
        int at = i + off;
        return at >= 0 && at < entries.Count ? entries[at] : "";
    }

    private static string LogRowsLine(IReadOnlyList<string> entries, int line, LogNode node)
    {
        // 整行淘汰(镜像 kit.LogWindow._render_rows):超上限丢最旧的整行,
        // 窗口对齐行边界,最新永远在最后一行
        int cap = node.Lines.Length * node.PerLine;
        int begin = 0;
        if (entries.Count > cap)
        {
            begin = entries.Count - cap;
            begin += (entries.Count - begin) % node.PerLine;
        }
        int start = begin + line * node.PerLine;
        if (start >= entries.Count) return "";
        var parts = new List<string>();
        for (int k = start; k < Math.Min(start + node.PerLine, entries.Count); k++)
            parts.Add(entries[k]);
        return string.Join(node.Sep, parts);
    }

    private void UpdateSprites(SurfaceView v)
    {
        foreach (var s in _sprites)
        {
            int frame = (int)(v.Snap.T * s.Fps) % Math.Max(1, s.Frames);
            double fh = s.FrameH / s.SheetH;
            SetFrame(s.Layer, Sel("setContentsRect:"),
                new CGRect(0, 1 - (frame + 1) * fh, s.FrameW / s.SheetW, fh));
        }
    }

    // ================================================================ 空间映射

    /// <summary>主题顶左 y + 元素高(均主题单位)→ layer 底左 y(翻转)。
    /// 单位纪律(2026-09-02 定位的真 bug):h 必须是主题单位 —— 传已缩放值
    /// 会把元素压低 h×(scale−1),「越高越偏」的非均匀垂直错位即此。</summary>
    private double MapY(double y, double h) => _bh - _oy - Sc(y + h);

    /// <summary>元素矩形(全部主题单位):根下做空间映射,strip 子元素用父层
    /// 局部坐标(classic 的 screen 空间 _scale=1,两语义合一)。</summary>
    private CGRect MapRel(IntPtr parent, double x, double y, double w, double h)
    {
        if (parent != _root && parent != IntPtr.Zero)
        {
            CGRect pf = ObjcRect(parent, Sel("frame"));
            return new CGRect(Sc(x), pf.Height - Sc(y + h), Sc(w), Sc(h));
        }
        return new CGRect(_ox + Sc(x), MapY(y, h), Sc(w), Sc(h));
    }

    private double Sc(double v) => v * _scale;

    private static double ColumnAlpha(int i, int lines, double a) =>
        Math.Min(1.0, a * (0.3 + 0.65 * (i + 1.0) / lines));

    /// <summary>暗化:tint 分量乘法(0.5 灰 = 减半)。</summary>
    private static ThemeColor Mul(ThemeColor a, ThemeColor b) =>
        new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

    private static ThemeColor Half(ThemeColor c) => Mul(c, new ThemeColor(0.5, 0.5, 0.5, 1));

    // ================================================================ 层工厂

    private IntPtr AddImageLayer(IntPtr parent, IntPtr cgImage, double x, double y,
        double w, double h, bool nearest)
    {
        IntPtr l = NewLayer();
        Transaction(() =>
        {
            SetFrame(l, Sel("setFrame:"), new CGRect(x, y, w, h));
            ObjcIdPtr(l, Sel("setContents:"), cgImage);
            if (nearest)
            {
                IntPtr f = CfString("nearest");
                ObjcIdPtr(l, Sel("setMagnificationFilter:"), f);
                CfRelease(f);
            }
        });
        ObjcLayer(parent, Sel("addSublayer:"), l);
        _statics.Add(l);
        return l;
    }

    private IntPtr AddColorLayer(IntPtr parent, double x, double y, double w, double h, ThemeColor color)
    {
        IntPtr l = NewLayer();
        Transaction(() =>
        {
            SetFrame(l, Sel("setFrame:"), new CGRect(x, y, w, h));
            ObjcVoidColor(l, Sel("setBackgroundColor:"), Color(color));
        });
        ObjcLayer(parent, Sel("addSublayer:"), l);
        return l;
    }

    private IntPtr AddDarkImageCopy(IntPtr container, IntPtr img, double x, double y,
        double w, double h, ThemeColor tint)
    {
        // 半透明黑底 + 贴图:CALayer 的 backgroundColor 画在 contents 之下,
        // 合成 ≈ 灰阶素材乘 tint(与 gd 的 ×FILL_TINT 视觉近似)
        IntPtr l = NewLayer();
        Transaction(() =>
        {
            SetFrame(l, Sel("setFrame:"), new CGRect(x, y, w, h));
            ObjcVoidColor(l, Sel("setBackgroundColor:"),
                Color(new ThemeColor(1 - tint.R, 1 - tint.G, 1 - tint.B, 0.5)));
            ObjcIdPtr(l, Sel("setContents:"), img);
        });
        ObjcLayer(container, Sel("addSublayer:"), l);
        return l;
    }

    private IntPtr AddTextLayer(IntPtr parent, string text, double x, double y,
        double w, double h, double fontPt, ThemeColor color, int? align)
    {
        IntPtr l = objc_msgSend(objc_getClass("CATextLayer"), Sel("alloc"));
        l = ObjcId(l, Sel("init"));
        Transaction(() =>
        {
            SetFrame(l, Sel("setFrame:"), new CGRect(x, y, Math.Max(w, 1), Math.Max(h, 4)));
            ObjcVoidD(l, Sel("setContentsScale:"), 2.0);
            ObjcVoidD(l, Sel("setFontSize:"), Sc(fontPt));
            ObjcVoidColor(l, Sel("setForegroundColor:"), Color(color));
            IntPtr mode = CfString(align == 1 ? "center" : align == 2 ? "right" : "left");
            ObjcIdPtr(l, Sel("setAlignmentMode:"), mode);
            CfRelease(mode);
            IntPtr cf = CfString(text);
            ObjcIdPtr(l, Sel("setString:"), cf);
            CfRelease(cf);
        });
        ObjcLayer(parent, Sel("addSublayer:"), l);
        return l;
    }

    // ================================================================ 细条兜底

    private void BuildThinBar(double bw, double bh)
    {
        _thin = true;
        double trackW = bw * 0.66;
        double x = (bw - trackW) / 2;
        var track = NewLayer();
        _thinFill = NewLayer();
        IntPtr text = objc_msgSend(objc_getClass("CATextLayer"), Sel("alloc"));
        text = ObjcId(text, Sel("init"));
        Transaction(() =>
        {
            SetFrame(track, Sel("setFrame:"), new CGRect(x, 14, trackW, 6));
            ObjcVoidD(track, Sel("setCornerRadius:"), 3);
            ObjcVoidColor(track, Sel("setBackgroundColor:"), Color(0, 0, 0, 0.55));
            SetFrame(_thinFill, Sel("setFrame:"), new CGRect(0, 0, 1, 6));
            ObjcVoidColor(_thinFill, Sel("setBackgroundColor:"), Color(0.40, 0.85, 1.0, 0.95));
            ObjcLayer(track, Sel("addSublayer:"), _thinFill);
            SetFrame(text, Sel("setFrame:"), new CGRect(x, 24, trackW, 20));
            ObjcVoidD(text, Sel("setContentsScale:"), 2.0);
            ObjcVoidD(text, Sel("setFontSize:"), 13.0);
            ObjcVoidColor(text, Sel("setForegroundColor:"), Color(1, 1, 1, 0.92));
            IntPtr center = CfString("center");
            ObjcIdPtr(text, Sel("setAlignmentMode:"), center);
            CfRelease(center);
        });
        ObjcLayer(_root, Sel("addSublayer:"), track);
        ObjcLayer(_root, Sel("addSublayer:"), text);
        _thinText = text;
        _thinTrackW = trackW;
    }

    private void PresentThin(SurfaceView v)
    {
        double frac = Math.Clamp(double.IsNaN(v.Snap.Overall) ? 0 : v.Snap.Overall, 0, 1);
        // StepText 已含阶段包装(LoadingPresentation),不再叠 [n/7] 前缀
        string label = v.Snap.StepText != "" ? v.Snap.StepText : v.Snap.DetailText;
        IntPtr cf = CfString(label);
        Transaction(() =>
        {
            SetFrame(_thinFill, Sel("setFrame:"), new CGRect(0, 0, Math.Max(1, _thinTrackW * frac), 6));
            ObjcIdPtr(_thinText, Sel("setString:"), cf);
        });
        if (cf != IntPtr.Zero) CfRelease(cf);
    }

    // ================================================================ 互操作

    private static void Transaction(Action body)
    {
        IntPtr cat = objc_getClass("CATransaction");
        ObjcVoid(cat, Sel("begin"));
        ObjcVoidB(cat, Sel("setDisableActions:"), true);
        body();
        ObjcVoid(cat, Sel("commit"));
        // Steam 客户端启动的上下文里显式 commit 可能不直达渲染服务器(直启可以);
        // flush 强制同步送达 —— 2026-09-02 诊断中加入,证实后保留或移除
        ObjcVoid(cat, Sel("flush"));
    }

    private static IntPtr NewLayer()
    {
        IntPtr l = objc_msgSend(objc_getClass("CALayer"), Sel("alloc"));
        return ObjcId(l, Sel("init"));
    }

    private static IntPtr _lastWindow;

    private static IntPtr GameLayer()
    {
        IntPtr nsAppClass = objc_getClass("NSApplication");
        IntPtr app = nsAppClass == IntPtr.Zero
            ? IntPtr.Zero
            : ObjcId(nsAppClass, Sel("sharedApplication"));
        IntPtr win = app == IntPtr.Zero ? IntPtr.Zero : ObjcId(app, Sel("mainWindow"));
        if (win == IntPtr.Zero)
        {
            IntPtr list = app == IntPtr.Zero ? IntPtr.Zero : ObjcId(app, Sel("windows"));
            if (list != IntPtr.Zero && ObjcCount(list, Sel("count")) > 0)
            {
                win = ObjcIdAt(list, Sel("objectAtIndex:"), 0);
            }
        }
        if (win == IntPtr.Zero) return IntPtr.Zero;
        _lastWindow = win;
        IntPtr top = ObjcId(ObjcId(win, Sel("contentView")), Sel("layer"));
        return FindRenderLayer(top);
    }

    /// <summary>
    /// 定位真正渲染的 CAMetalLayer(2026-09-02 实证):Steam 客户端启动时
    /// contentView.layer 是覆盖层包装(bounds=点空间,子层几何被按点解释),
    /// 真正渲染的 Metal 层在其子层里(drawable 空间);直启时 contentView.layer
    /// 就是渲染层本身。策略:向下降到「最深的 drawableSize&gt;0 的子层」,
    /// 并把沿途层树打进日志(类名 + bounds + drawable)留证。
    /// </summary>
    private static IntPtr FindRenderLayer(IntPtr top)
    {
        IntPtr layer = top;
        IntPtr selDrawable = Sel("drawableSize");
        var trail = new System.Text.StringBuilder();
        for (int depth = 0; depth < 4 && layer != IntPtr.Zero; depth++)
        {
            CGRect b = ObjcRect(layer, Sel("bounds"));
            // respondsToSelector 守卫:全屏过渡时系统会塞入自己的动画层(普通
            // CALayer),对它们调 drawableSize = 未识别选择子 → SIGABRT(实证)
            bool metal = RespondsTo(layer, selDrawable);
            trail.Append($"[{depth}]{ClassName(layer)} b={b.Width:F0}x{b.Height:F0} " +
                         $"{(metal ? "metal" : "plain")} ");
            IntPtr subs = ObjcId(layer, Sel("sublayers"));
            IntPtr next = IntPtr.Zero;
            if (subs != IntPtr.Zero)
            {
                ulong n = ObjcCount(subs, Sel("count"));
                if (n > 8) n = 8; // 只找前几个,层树可能很深
                for (ulong k = 0; k < n; k++)
                {
                    IntPtr c = ObjcIdAt(subs, Sel("objectAtIndex:"), k);
                    if (c == IntPtr.Zero || !RespondsTo(c, selDrawable)) continue;
                    CGSize cd = ObjcSize(c, selDrawable);
                    if (cd.Width > 1 && cd.Height > 1) next = c;
                }
            }
            if (next == IntPtr.Zero) break;
            layer = next;
        }
        if (_diagEnv) Log.Warn($"[ItsLoading][overlay][tree] {trail}→ attach {ClassName(layer)}");
        return layer;
    }

    private static bool RespondsTo(IntPtr obj, IntPtr sel) =>
        obj != IntPtr.Zero && ObjcRespondsSelector(obj, Sel("respondsToSelector:"), sel);

    private static bool _diagEnv =
        System.Environment.GetEnvironmentVariable("ITSLOADING_PROBE_LAYER") == "1";

    private static string ClassName(IntPtr obj) =>
        obj == IntPtr.Zero ? "nil" : Marshal.PtrToStringAnsi(object_getClassName(obj)) ?? "?";

    /// <summary>
    /// 2026-09-02 临时几何诊断:无条件打全所有尺寸 API 的读数,并用品红边框
    /// 把「本代码假定的画布」画在屏幕上 —— 用户截图对照即可定出子层真实空间。
    /// </summary>
    private void LogGeometryDiag(string where)
    {
        try
        {
            CGRect lb = ObjcRect(_layer, Sel("bounds"));
            CGRect lf = ObjcRect(_layer, Sel("frame"));
            IntPtr selDrawable = Sel("drawableSize");
            CGSize ds = RespondsTo(_layer, selDrawable) ? ObjcSize(_layer, selDrawable) : default;
            double cs = ObjcDouble(_layer, Sel("contentsScale"));
            CGRect wf = _lastWindow != IntPtr.Zero ? ObjcRect(_lastWindow, Sel("frame")) : default;
            double bs = _lastWindow != IntPtr.Zero ? ObjcDouble(_lastWindow, Sel("backingScaleFactor")) : 0;
            Log.Warn($"[ItsLoading][overlay][geom]{where} " +
                     $"layer.bounds={lb.Width:F0}x{lb.Height:F0} " +
                     $"layer.frame=({lf.X:F0},{lf.Y:F0},{lf.Width:F0}x{lf.Height:F0}) " +
                     $"drawable={ds.Width:F0}x{ds.Height:F0} layer.cs={cs:F1} " +
                     $"win.frame=({wf.X:F0},{wf.Y:F0},{wf.Width:F0}x{wf.Height:F0}) " +
                     $"win.backing={bs:F1} → 我方画布 {_bw:F0}x{_bh:F0}");
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading][overlay][geom]{where} failed: {e.Message}");
        }
    }

    /// <summary>素材装载:NSImage(PNG bytes)→ CGImage,按 src 缓存;缺席 = Zero。</summary>
    private IntPtr LoadImage(string src)
    {
        if (_images.TryGetValue(src, out IntPtr cached)) return cached;
        IntPtr result = IntPtr.Zero;
        try
        {
            // 路径防逃逸(外部主题不可信):仅允许主题目录内的相对路径
            if (src.StartsWith("/") || src.Contains(".."))
            {
                _images[src] = IntPtr.Zero;
                return IntPtr.Zero;
            }
            string path = Path.Combine(_themeDir, src);
            if (File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                IntPtr data = CfDataCreate(IntPtr.Zero, bytes, (nint)bytes.LongLength);
                if (data != IntPtr.Zero)
                {
                    IntPtr img = objc_msgSend(objc_getClass("NSImage"), Sel("alloc"));
                    img = ObjcIdPtrRet(img, Sel("initWithData:"), data);
                    CfRelease(data);
                    if (img != IntPtr.Zero)
                    {
                        result = ObjcId(img, Sel("CGImage"));
                        ObjcVoid(img, Sel("release"));
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading][overlay] image load failed ({src}): {e.Message}");
        }
        _images[src] = result;
        return result;
    }

    private const string LibObjc = "libobjc.A.dylib";
    // 框架库在 coreclr 的默认 dlopen 搜索下解析不到(libdyld 同款问题),按完整路径点名
    private const string LibCf =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibCg =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(LibObjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjc, EntryPoint = "object_getClassName")]
    private static extern IntPtr object_getClassName(IntPtr obj);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcId(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcVoid(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcVoidB(IntPtr self, IntPtr sel,
        [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcVoidD(IntPtr self, IntPtr sel, double arg);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcVoidColor(IntPtr self, IntPtr sel, IntPtr color);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcIdPtr(IntPtr self, IntPtr sel, IntPtr arg);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcIdPtrRet(IntPtr self, IntPtr sel, IntPtr arg);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void ObjcLayer(IntPtr self, IntPtr sel, IntPtr layer);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern void SetFrame(IntPtr self, IntPtr sel, CGRect rect);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ObjcRespondsSelector(IntPtr self, IntPtr sel, IntPtr arg);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcIdAt(IntPtr self, IntPtr sel, ulong index);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern ulong ObjcCount(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern CGRect ObjcRect(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern CGSize ObjcSize(IntPtr self, IntPtr sel);

    [DllImport(LibObjc, EntryPoint = "objc_msgSend")]
    private static extern double ObjcDouble(IntPtr self, IntPtr sel);

    [DllImport(LibCg, EntryPoint = "CGColorCreateSRGB")]
    private static extern IntPtr Color(double r, double g, double b, double a);

    private static IntPtr Color(ThemeColor c) => Color(c.R, c.G, c.B, c.A);

    [DllImport(LibCg, EntryPoint = "CGImageGetWidth")]
    private static extern nuint CGImageGetWidth(IntPtr image);

    [DllImport(LibCg, EntryPoint = "CGImageGetHeight")]
    private static extern nuint CGImageGetHeight(IntPtr image);

    [DllImport(LibCf, EntryPoint = "CFStringCreateWithCString")]
    private static extern IntPtr CfStringCreate(IntPtr alloc, byte[] utf8, uint encoding);

    [DllImport(LibCf, EntryPoint = "CFDataCreate")]
    private static extern IntPtr CfDataCreate(IntPtr alloc, byte[] bytes, nint length);

    [DllImport(LibCf, EntryPoint = "CFRelease")]
    private static extern void CfRelease(IntPtr cf);

    private static IntPtr CfString(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        return CfStringCreate(IntPtr.Zero, bytes, 0x08000100); // kCFStringEncodingUTF8
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;

        public CGRect(double x, double y, double w, double h)
        {
            X = x; Y = y; Width = w; Height = h;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width, Height;
    }
}
