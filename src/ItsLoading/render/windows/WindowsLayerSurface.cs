using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using MegaCrit.Sts2.Core.Logging;

#nullable enable

namespace ItsLoading;

/// <summary>
/// Windows 冻结呈现面。透明 owned layered window 由独立 UI 线程拥有，主题用
/// GDI+ 画入 32-bit DIB，再由 UpdateLayeredWindow 直接交给桌面合成器。因此游戏
/// 主线程或 Godot/D3D 呈现停住时，indeterminate bar 与 sprite 仍会自行运动。
/// </summary>
internal sealed class WindowsLayerSurface : IThemeSurface
{
    private readonly ThemePlan? _plan;
    private readonly string _themeDir;
    private readonly string _version;
    private readonly Func<string, string> _txt;
    private readonly bool _calib;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, IntPtr> _images = new();

    private Thread? _thread;
    private IntPtr _game, _overlay, _dc, _dib, _oldBitmap, _bits, _graphics, _fontFamily;
    private nuint _gdipToken;
    private int _width, _height;
    private bool _ready, _stop, _dirty;
    private byte _opacity = 255;
    private LoadingFrame _frame;
    private bool _hasFrame;
    private double _activityOffset;

    internal WindowsLayerSurface(ThemePlan? plan, string themeDir, string version,
        Func<string, string>? txt = null, bool calib = false)
    {
        _plan = plan;
        _themeDir = themeDir;
        _version = version;
        _txt = txt ?? (k => k);
        _calib = calib;
    }

    public bool TryAttach()
    {
        _game = FindGameWindow();
        if (_game == IntPtr.Zero)
        {
            Log.Warn("[ItsLoading][overlay] Windows game window not found — inert this boot");
            return false;
        }
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "ItsLoading.WinOverlay" };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(3)) || !_ready)
        {
            Teardown();
            Log.Warn("[ItsLoading][overlay] Windows layered window failed to start — inert this boot");
            return false;
        }
        Log.Warn("[ItsLoading][overlay] Windows layered window attached");
        return true;
    }

    public void Present(LoadingFrame frame)
    {
        lock (_gate)
        {
            _frame = frame;
            _hasFrame = true;
            foreach (SpriteElement sprite in _plan?.Elements.OfType<SpriteElement>()
                         ?? Enumerable.Empty<SpriteElement>())
                _activityOffset += (sprite.Activity?.FramesPerUpdate ?? 0) / Math.Max(0.1, sprite.Fps);
            _dirty = true;
        }
        _wake.Set();
    }

    public void SetOpacity(double opacity)
    {
        lock (_gate)
        {
            _opacity = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
            _dirty = true;
        }
        _wake.Set();
    }

    public void Teardown()
    {
        lock (_gate) _stop = true;
        _wake.Set();
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
        _thread = null;
    }

    private void RenderLoop()
    {
        try
        {
            var input = new GdiplusStartupInput { Version = 1 };
            if (GdiplusStartup(out _gdipToken, ref input, IntPtr.Zero) != 0)
                throw new InvalidOperationException("GDI+ startup failed");
            GdipGetGenericFontFamilySansSerif(out _fontFamily);
            _overlay = CreateWindowExW(ExLayered | ExTransparent | ExNoActivate | ExToolWindow,
                "STATIC", "ItsLoading native overlay", WsPopup, 0, 0, 1, 1,
                _game, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_overlay == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
            _ready = true;
            _started.Set();

            while (true)
            {
                while (PeekMessageW(out Msg msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                LoadingFrame frame;
                bool hasFrame, dirty, stop;
                byte opacity;
                lock (_gate)
                {
                    frame = _frame;
                    hasFrame = _hasFrame;
                    dirty = _dirty;
                    _dirty = false;
                    stop = _stop;
                    opacity = _opacity;
                }
                if (stop) break;
                bool moving = hasFrame && HasMotion(frame);
                if ((dirty || moving) && PositionOverlay() && hasFrame)
                    DrawAndCommit(frame, opacity);
                _wake.WaitOne(moving ? 33 : 250);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading][overlay] Windows renderer stopped: {e.Message}");
        }
        finally
        {
            _ready = false;
            _started.Set();
            ReleaseGraphics();
            foreach (IntPtr image in _images.Values)
                if (image != IntPtr.Zero) GdipDisposeImage(image);
            _images.Clear();
            if (_overlay != IntPtr.Zero) DestroyWindow(_overlay);
            _overlay = IntPtr.Zero;
            if (_fontFamily != IntPtr.Zero) GdipDeleteFontFamily(_fontFamily);
            _fontFamily = IntPtr.Zero;
            if (_gdipToken != 0) GdiplusShutdown(_gdipToken);
            _gdipToken = 0;
        }
    }

    private bool HasMotion(LoadingFrame frame) =>
        frame.LocalIndeterminate || (_plan?.Elements.Any(e => e is SpriteElement) ?? false);

    private bool PositionOverlay()
    {
        if (!IsWindow(_game) || IsIconic(_game) || !GetClientRect(_game, out Rect r))
        {
            ShowWindow(_overlay, 0);
            return false;
        }
        var point = new Point(r.Left, r.Top);
        if (!ClientToScreen(_game, ref point)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 50 || h < 50) return false;
        SetWindowPos(_overlay, IntPtr.Zero, point.X, point.Y, w, h,
            SwpNoActivate | SwpShowWindow);
        if (w != _width || h != _height) CreateGraphics(w, h);
        return _graphics != IntPtr.Zero;
    }

    private void CreateGraphics(int width, int height)
    {
        ReleaseGraphics();
        _width = width;
        _height = height;
        _dc = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, Compression = 0,
            },
        };
        _dib = CreateDIBSection(_dc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dc == IntPtr.Zero || _dib == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDIBSection failed ({Marshal.GetLastWin32Error()})");
        _oldBitmap = SelectObject(_dc, _dib);
        Check(GdipCreateFromHDC(_dc, out _graphics), "GdipCreateFromHDC");
        GdipSetSmoothingMode(_graphics, 4);
        GdipSetInterpolationMode(_graphics, 7);
        GdipSetTextRenderingHint(_graphics, 3);
    }

    private void ReleaseGraphics()
    {
        if (_graphics != IntPtr.Zero) GdipDeleteGraphics(_graphics);
        _graphics = IntPtr.Zero;
        if (_dc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) SelectObject(_dc, _oldBitmap);
        _oldBitmap = IntPtr.Zero;
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        _dib = IntPtr.Zero;
        if (_dc != IntPtr.Zero) DeleteDC(_dc);
        _dc = IntPtr.Zero;
        _bits = IntPtr.Zero;
    }

    private void DrawAndCommit(LoadingFrame frame, byte opacity)
    {
        GdipSetCompositingMode(_graphics, 1);
        GdipGraphicsClear(_graphics, 0);
        GdipSetCompositingMode(_graphics, 0);
        if (_plan == null) DrawThin(frame);
        else
        {
            foreach (ThemeElementDef element in _plan.Elements) DrawElement(element, frame);
            if (_calib) DrawCalibration();
        }
        var dst = new Point();
        ClientToScreen(_game, ref dst);
        var size = new Size(_width, _height);
        var src = new Point();
        var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
        if (!UpdateLayeredWindow(_overlay, IntPtr.Zero, ref dst, ref size, _dc,
                ref src, 0, ref blend, 2))
            throw new InvalidOperationException($"UpdateLayeredWindow failed ({Marshal.GetLastWin32Error()})");
    }

    private void DrawElement(ThemeElementDef e, LoadingFrame frame)
    {
        (double scale, double ox, double oy) = Transform();
        double ParentY() => e.Parent is { } id
            && _plan!.Elements.FirstOrDefault(x => x.Id == id) is StripElement strip
                ? _height - strip.H * scale : oy;
        double X(double x) => ox + x * scale;
        double Y(double y) => ParentY() + y * scale;
        double S(double v) => v * scale;
        switch (e)
        {
            case BgElement bg:
                Fill(0, 0, _width, _height, bg.Color);
                break;
            case LogoElement logo:
            {
                IntPtr image = Image(logo.Src);
                if (image != IntPtr.Zero)
                {
                    GdipGetImageWidth(image, out uint iw);
                    GdipGetImageHeight(image, out uint ih);
                    double h = iw == 0 ? logo.W : logo.W * ih / iw;
                    DrawImage(image, X(logo.X), Y(logo.Y), S(logo.W), S(h), 0, 0, iw, ih, null);
                }
                else
                    Text(logo.FallbackText, X(logo.X), Y(logo.Y), S(logo.W),
                        S(logo.FallbackFont * 1.4 + 6), S(logo.FallbackFont), logo.FallbackColor, 0);
                break;
            }
            case LabelElement label:
            {
                string value = label.Bind switch
                {
                    ThemeBind.Step => frame.StepText,
                    ThemeBind.Detail => frame.DetailText,
                    _ => label.Text.Resolve(_txt),
                };
                Text(value, X(label.X), Y(label.Y),
                    label.W.HasValue ? S(label.W.Value) : _width - X(label.X),
                    S(label.H ?? label.Font * 1.4 + 6), S(label.Font), label.Color, label.Align ?? 0);
                break;
            }
            case VersionLabelElement version:
                Text(version.Prefix + _version, X(version.X), Y(version.Y),
                    version.W.HasValue ? S(version.W.Value) : _width - X(version.X),
                    S(version.H ?? version.Font * 1.4 + 6), S(version.Font),
                    version.Color, version.Align ?? 0);
                break;
            case BarSolidElement bar:
                DrawBar(bar, frame, X(bar.X), Y(bar.Y), BarWidth(bar, scale), S(bar.H),
                    bar.Track, 0, 0);
                break;
            case BarOutlineElement bar:
                DrawBar(bar, frame, X(bar.X), Y(bar.Y), BarWidth(bar, scale), S(bar.H),
                    bar.Border, S(bar.BorderW), S(bar.Inset));
                break;
            case IconRowElement row:
                DrawRow(row, frame, null);
                break;
            case DotsElement dots:
                DrawDots(dots, null);
                break;
            case MaskTrackElement mask:
                DrawMask(mask, frame);
                break;
            case SpriteElement sprite:
                DrawSprite(sprite);
                break;
            case LogColumnElement log:
                DrawLog(log, frame.Log, true);
                break;
            case LogRowsElement log:
                DrawLog(log, frame.Log, false);
                break;
        }
    }

    private double BarWidth(BarElementDef bar, double scale)
    {
        if (!bar.W.IsFill) return bar.W.Value * scale;
        return _width - 2 * bar.X * scale;
    }

    private void DrawBar(BarElementDef bar, LoadingFrame frame, double x, double y,
        double w, double h, ThemeColor edge, double border, double inset)
    {
        if (bar is BarSolidElement) Fill(x, y, w, h, edge);
        else
        {
            Fill(x, y, w, border, edge); Fill(x, y + h - border, w, border, edge);
            Fill(x, y, border, h, edge); Fill(x + w - border, y, border, h, edge);
        }
        double track = Math.Max(1, w - 2 * inset), fillX = x + inset, fillW;
        if (bar.Bind == ThemeBind.Local && frame.LocalIndeterminate && bar.Indeterminate != null)
        {
            (double off, double width) = Indeterminate(track, bar.Indeterminate, _clock.Elapsed.TotalSeconds);
            fillX += off;
            fillW = width;
        }
        else
        {
            double value = bar.Bind == ThemeBind.Overall ? frame.Overall : frame.Local;
            fillW = Math.Max(1, Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1) * track);
        }
        Fill(fillX, y + inset, fillW, Math.Max(1, h - 2 * inset), bar.Fill);
    }

    internal static (double Offset, double Width) Indeterminate(double track,
        IndeterminateDef ind, double seconds)
    {
        if (ind.Mode == IndeterminateMode.Pulse)
        {
            double min = Math.Clamp(ind.MinW, 1, track);
            double max = Math.Clamp(min + ind.Travel, min, track);
            double pulse = Math.Abs(seconds % 2.5 / 1.25 - 1);
            return (0, min + (max - min) * pulse);
        }
        double width = Math.Max(1, track * 0.25);
        double slide = seconds % Math.Max(0.1, ind.CycleS) / Math.Max(0.1, ind.CycleS);
        return ((track - width) * slide, width);
    }

    private void DrawRow(IconRowElement row, LoadingFrame frame, ThemeColor? tint)
    {
        if (!_plan!.Rows.TryGetValue(row.Id, out IconRowPlan? plan)) return;
        int active = Math.Clamp((int)frame.Stage, 1, row.Count) - 1;
        for (int i = 0; i < plan.Slots.Count; i++)
        {
            ThemeRect slot = plan.Slots[i];
            double factor = row.Enlarge != null && i == active ? row.Enlarge.Factor : 1;
            double size = slot.W * factor;
            double x = slot.X + slot.W / 2 - size / 2;
            double y = row.Pivot == "bottom"
                ? slot.Y + slot.H - size : slot.Y + slot.H / 2 - size / 2;
            RectD r = Map(x, y, size, size);
            IntPtr image = Image(plan.Sources[i]);
            if (image == IntPtr.Zero)
                Fill(r.X, r.Y, r.W, r.H, tint ?? row.Placeholder ?? new ThemeColor(.5, .5, .5, 1));
            else
            {
                GdipGetImageWidth(image, out uint iw); GdipGetImageHeight(image, out uint ih);
                DrawImage(image, r.X, r.Y, r.W, r.H, 0, 0, iw, ih, tint);
            }
        }
    }

    private void DrawDots(DotsElement dots, ThemeColor? tint)
    {
        if (!_plan!.DotSets.TryGetValue(dots.Id, out DotsPlan? plan)) return;
        ThemeColor color = tint.HasValue ? Multiply(dots.Color, tint.Value) : dots.Color;
        foreach (ThemeRect dot in plan.Dots)
        {
            RectD r = Map(dot.X, dot.Y, dot.W, dot.H);
            Ellipse(r.X, r.Y, r.W, r.H, color);
        }
    }

    private void DrawMask(MaskTrackElement mask, LoadingFrame frame)
    {
        if (!_plan!.Masks.TryGetValue(mask.Id, out MaskTrackPlan? plan)) return;
        RectD domain = Map(plan.Domain.X, plan.Domain.Y, plan.Domain.W, plan.Domain.H);
        double width;
        if (frame.LocalIndeterminate)
        {
            (double off, double w) = Indeterminate(domain.W, mask.Indeterminate,
                _clock.Elapsed.TotalSeconds);
            GdipSetClipRectI(_graphics, (int)(domain.X + off), (int)domain.Y,
                Math.Max(1, (int)w), Math.Max(1, (int)domain.H), 0);
            width = w;
        }
        else
        {
            width = Math.Clamp(double.IsNaN(frame.Local) ? 0 : frame.Local, 0, 1) * domain.W;
            GdipSetClipRectI(_graphics, (int)domain.X, (int)domain.Y,
                Math.Max(1, (int)width), Math.Max(1, (int)domain.H), 0);
        }
        if (width > 0)
            foreach (string id in mask.Members)
            {
                ThemeElementDef? member = _plan.Elements.FirstOrDefault(e => e.Id == id);
                if (member is IconRowElement row) DrawRow(row, frame, mask.Tint);
                else if (member is DotsElement dots) DrawDots(dots, mask.Tint);
            }
        GdipResetClip(_graphics);
    }

    private void DrawSprite(SpriteElement sprite)
    {
        IntPtr image = Image(sprite.Src);
        if (image == IntPtr.Zero) return;
        double activity;
        lock (_gate) activity = _activityOffset;
        int at = (int)Math.Floor((_clock.Elapsed.TotalSeconds + activity) * sprite.Fps)
            % Math.Max(1, sprite.Frames);
        RectD r = Map(sprite.X, sprite.Y, sprite.W, sprite.H);
        DrawImage(image, r.X, r.Y, r.W, r.H, 0, at * sprite.FrameH,
            sprite.FrameW, sprite.FrameH, null);
    }

    private void DrawLog(LogElementDef log, IReadOnlyList<string> entries, bool column)
    {
        int perLine = column ? 1 : ((LogRowsElement)log).PerLine;
        string sep = column ? "" : ((LogRowsElement)log).Sep;
        for (int line = 0; line < log.Lines; line++)
        {
            string value = column ? LogColumnLine(entries, line, log.Lines)
                : LogRowsLine(entries, line, log.Lines, perLine, sep);
            ThemeColor color = column
                ? log.Color with { A = ColumnAlpha(line, log.Lines, log.Color.A) } : log.Color;
            RectD r = Map(log.X, log.Y + line * log.LineH,
                column ? 0 : ((LogRowsElement)log).W, log.LineH);
            Text(value, r.X, r.Y, r.W == 0 ? _width - r.X : r.W, r.H,
                Transform().scale * log.Font, color, log.Align ?? (column ? 0 : 1));
        }
    }

    internal static string LogColumnLine(IReadOnlyList<string> entries, int line, int lines)
    {
        int at = line + entries.Count - lines;
        return at >= 0 && at < entries.Count ? entries[at] : "";
    }

    internal static string LogRowsLine(IReadOnlyList<string> entries, int line,
        int lines, int perLine, string sep)
    {
        int cap = lines * perLine, begin = 0;
        if (entries.Count > cap)
        {
            begin = entries.Count - cap;
            begin += (entries.Count - begin) % perLine;
        }
        int start = begin + line * perLine;
        if (start >= entries.Count) return "";
        return string.Join(sep, entries.Skip(start).Take(perLine));
    }

    private void DrawThin(LoadingFrame frame)
    {
        double w = _width * .66, x = (_width - w) / 2, y = _height - 20;
        Fill(x, y, w, 6, new ThemeColor(0, 0, 0, .55));
        Fill(x, y, Math.Max(1, w * Math.Clamp(double.IsNaN(frame.Overall) ? 0 : frame.Overall, 0, 1)),
            6, new ThemeColor(.4, .85, 1, .95));
        Text(frame.StepText != "" ? frame.StepText : frame.DetailText, x, y - 24, w, 20,
            13, new ThemeColor(1, 1, 1, .92), 1);
    }

    private void DrawCalibration()
    {
        if (_plan == null || !_plan.Space.IsDesign) return;
        (double s, double ox, double oy) = Transform();
        for (int i = 1; i < 10; i++)
        {
            double x = ox + _plan.Space.W * s * i / 10;
            double y = oy + _plan.Space.H * s * i / 10;
            Fill(x, oy, i == 5 ? 4 : 2, _plan.Space.H * s, new ThemeColor(1, 1, 1, i == 5 ? .7 : .3));
            Fill(ox, y, _plan.Space.W * s, i == 5 ? 4 : 2, new ThemeColor(1, 1, 1, i == 5 ? .7 : .3));
        }
    }

    private (double scale, double ox, double oy) Transform()
    {
        if (_plan?.Space.IsDesign != true) return (1, 0, 0);
        double scale = Math.Min(_width / _plan.Space.W, _height / _plan.Space.H);
        return (scale, (_width - _plan.Space.W * scale) / 2,
            (_height - _plan.Space.H * scale) / 2);
    }

    private RectD Map(double x, double y, double w, double h)
    {
        (double s, double ox, double oy) = Transform();
        return new RectD(ox + x * s, oy + y * s, w * s, h * s);
    }

    private void Fill(double x, double y, double w, double h, ThemeColor color)
    {
        if (w <= 0 || h <= 0 || color.A <= 0) return;
        GdipCreateSolidFill(Argb(color), out IntPtr brush);
        GdipFillRectangleI(_graphics, brush, (int)Math.Round(x), (int)Math.Round(y),
            Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
        GdipDeleteBrush(brush);
    }

    private void Ellipse(double x, double y, double w, double h, ThemeColor color)
    {
        GdipCreateSolidFill(Argb(color), out IntPtr brush);
        GdipFillEllipseI(_graphics, brush, (int)x, (int)y, Math.Max(1, (int)w), Math.Max(1, (int)h));
        GdipDeleteBrush(brush);
    }

    private void Text(string value, double x, double y, double w, double h,
        double fontSize, ThemeColor color, int align)
    {
        if (string.IsNullOrEmpty(value) || w <= 0 || h <= 0) return;
        GdipCreateFont(_fontFamily, (float)Math.Max(1, fontSize), 0, 2, out IntPtr font);
        GdipCreateStringFormat(0, 0, out IntPtr format);
        GdipSetStringFormatAlign(format, Math.Clamp(align, 0, 2));
        GdipCreateSolidFill(Argb(color), out IntPtr brush);
        var rect = new RectF((float)x, (float)y, (float)w, (float)h);
        GdipDrawString(_graphics, value, value.Length, font, ref rect, format, brush);
        GdipDeleteBrush(brush); GdipDeleteStringFormat(format); GdipDeleteFont(font);
    }

    private void DrawImage(IntPtr image, double x, double y, double w, double h,
        double sx, double sy, double sw, double sh, ThemeColor? tint)
    {
        IntPtr attrs = IntPtr.Zero, matrix = IntPtr.Zero;
        try
        {
            if (tint.HasValue)
            {
                GdipCreateImageAttributes(out attrs);
                ThemeColor c = tint.Value;
                float[] values =
                {
                    (float)c.R,0,0,0,0, 0,(float)c.G,0,0,0, 0,0,(float)c.B,0,0,
                    0,0,0,(float)c.A,0, 0,0,0,0,1,
                };
                matrix = Marshal.AllocHGlobal(values.Length * sizeof(float));
                Marshal.Copy(values, 0, matrix, values.Length);
                GdipSetImageAttributesColorMatrix(attrs, 1, true, matrix, IntPtr.Zero, 0);
            }
            GdipDrawImageRectRectI(_graphics, image, (int)x, (int)y,
                Math.Max(1, (int)w), Math.Max(1, (int)h), (int)sx, (int)sy,
                Math.Max(1, (int)sw), Math.Max(1, (int)sh), 2, attrs, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            if (attrs != IntPtr.Zero) GdipDisposeImageAttributes(attrs);
            if (matrix != IntPtr.Zero) Marshal.FreeHGlobal(matrix);
        }
    }

    private IntPtr Image(string src)
    {
        if (_images.TryGetValue(src, out IntPtr image)) return image;
        string path = Path.Combine(_themeDir, src);
        image = File.Exists(path) && GdipLoadImageFromFile(path, out IntPtr loaded) == 0
            ? loaded : IntPtr.Zero;
        _images[src] = image;
        return image;
    }

    private static uint Argb(ThemeColor c) =>
        (uint)(Math.Clamp((int)Math.Round(c.A * 255), 0, 255) << 24
            | Math.Clamp((int)Math.Round(c.R * 255), 0, 255) << 16
            | Math.Clamp((int)Math.Round(c.G * 255), 0, 255) << 8
            | Math.Clamp((int)Math.Round(c.B * 255), 0, 255));

    private static ThemeColor Multiply(ThemeColor a, ThemeColor b) =>
        new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

    private static double ColumnAlpha(int line, int lines, double alpha) =>
        Math.Min(1, alpha * (.3 + .65 * (line + 1.0) / lines));

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"{operation} failed ({status})");
    }

    private static IntPtr FindGameWindow()
    {
        uint pid = GetCurrentProcessId();
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid || !IsWindowVisible(hwnd) || !GetClientRect(hwnd, out Rect r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private readonly record struct RectD(double X, double Y, double W, double H);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int X, Y; public Size(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct RectF { public float X, Y, W, H; public RectF(float x, float y, float w, float h) { X=x; Y=y; W=w; H=h; } }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct GdiplusStartupInput { public uint Version; public IntPtr DebugEventCallback; public bool SuppressBackgroundThread; public bool SuppressExternalCodecs; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public Point Pt; public uint Private; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint ExLayered = 0x80000, ExTransparent = 0x20, ExNoActivate = 0x08000000,
        ExToolWindow = 0x80, WsPopup = 0x80000000, SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    [DllImport("user32", SetLastError=true)] private static extern IntPtr CreateWindowExW(uint ex, [MarshalAs(UnmanagedType.LPWStr)] string cls, [MarshalAs(UnmanagedType.LPWStr)] string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32", SetLastError=true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point dst, ref Size size, IntPtr srcDc, ref Point src, uint key, ref BlendFunction blend, uint flags);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] private static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32")] private static extern nint DispatchMessageW(ref Msg msg);
    [DllImport("kernel32")] private static extern uint GetCurrentProcessId();
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32", SetLastError=true)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdiplus")] private static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus")] private static extern void GdiplusShutdown(nuint token);
    [DllImport("gdiplus")] private static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);
    [DllImport("gdiplus")] private static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport("gdiplus")] private static extern int GdipGraphicsClear(IntPtr graphics, uint color);
    [DllImport("gdiplus")] private static extern int GdipSetCompositingMode(IntPtr graphics, int mode);
    [DllImport("gdiplus")] private static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);
    [DllImport("gdiplus")] private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);
    [DllImport("gdiplus")] private static extern int GdipSetTextRenderingHint(IntPtr graphics, int mode);
    [DllImport("gdiplus", CharSet=CharSet.Unicode)] private static extern int GdipLoadImageFromFile(string path, out IntPtr image);
    [DllImport("gdiplus")] private static extern int GdipDisposeImage(IntPtr image);
    [DllImport("gdiplus")] private static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport("gdiplus")] private static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport("gdiplus")] private static extern int GdipDrawImageRectRectI(IntPtr graphics, IntPtr image, int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh, int unit, IntPtr attrs, IntPtr callback, IntPtr data);
    [DllImport("gdiplus")] private static extern int GdipCreateSolidFill(uint color, out IntPtr brush);
    [DllImport("gdiplus")] private static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport("gdiplus")] private static extern int GdipFillRectangleI(IntPtr graphics, IntPtr brush, int x, int y, int w, int h);
    [DllImport("gdiplus")] private static extern int GdipFillEllipseI(IntPtr graphics, IntPtr brush, int x, int y, int w, int h);
    [DllImport("gdiplus")] private static extern int GdipGetGenericFontFamilySansSerif(out IntPtr family);
    [DllImport("gdiplus")] private static extern int GdipDeleteFontFamily(IntPtr family);
    [DllImport("gdiplus")] private static extern int GdipCreateFont(IntPtr family, float size, int style, int unit, out IntPtr font);
    [DllImport("gdiplus")] private static extern int GdipDeleteFont(IntPtr font);
    [DllImport("gdiplus")] private static extern int GdipCreateStringFormat(int attrs, ushort lang, out IntPtr format);
    [DllImport("gdiplus")] private static extern int GdipSetStringFormatAlign(IntPtr format, int align);
    [DllImport("gdiplus")] private static extern int GdipDeleteStringFormat(IntPtr format);
    [DllImport("gdiplus", CharSet=CharSet.Unicode)] private static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF rect, IntPtr format, IntPtr brush);
    [DllImport("gdiplus")] private static extern int GdipSetClipRectI(IntPtr graphics, int x, int y, int w, int h, int combineMode);
    [DllImport("gdiplus")] private static extern int GdipResetClip(IntPtr graphics);
    [DllImport("gdiplus")] private static extern int GdipCreateImageAttributes(out IntPtr attrs);
    [DllImport("gdiplus")] private static extern int GdipDisposeImageAttributes(IntPtr attrs);
    [DllImport("gdiplus")] private static extern int GdipSetImageAttributesColorMatrix(IntPtr attrs, int type, bool enable, IntPtr matrix, IntPtr gray, int flags);
}
