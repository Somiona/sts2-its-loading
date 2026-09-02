using System;
using System.Collections.Generic;

namespace ItsLoading;

/// <summary>
/// (Debug)开发者标定视图的矩形规则(单一事实源):theme.json 声明值 →
/// 各元素的标定矩形 [x, y, w, h](主题单位)。规则刻意与视觉字形无关 ——
/// 两个渲染器(CALayer 呈现面 / gd interpreter)用同一套矩形,截图才能
/// 程序化比对布局(2026-09-02 就是这样定位单位纪律 bug 的)。
///
/// ⚠️ gd 侧 interpreter.gd 的 _calib_* 段镜像同一套规则(跨运行时无法共享
/// 代码);改这里的任何估计值,必须同步 gd 侧并在两侧头注互指。
/// 消费方:MacLayerSurface.BuildCalibBoxes(绘制);估计规则:
///   文本类     w 缺省 240,h 缺省 font×1.4+6
///   logo       h = w×0.3(与贴图实际比例无关,固定估计)
///   bar        fill 宽 = 设计空间宽 − 2x
///   icon_row   行包络(span × size,底锚或中锚)
///   mask_track 用首个 icon_row 成员的行框
///   log_column 宽 240(与文本类一致)
/// </summary>
internal static class CalibRules
{
    internal readonly record struct Box(string Id, double X, double Y, double W, double H);

    internal static List<Box> Boxes(ThemeDef def)
    {
        var rowsById = new Dictionary<string, IconRowElement>();
        foreach (var e in def.Elements)
            if (e is IconRowElement r) rowsById[e.Id] = r;

        var outBoxes = new List<Box>();
        foreach (var e in def.Elements)
        {
            double[]? rect = e switch
            {
                BgElement or StripElement or DotsElement => null, // bg=画布边框;strip=容器;dots 由行框覆盖
                MaskTrackElement m => FirstRowRect(m, rowsById),
                LabelElement l => Text(l.X, l.Y, l.W, l.H, l.Font),
                VersionLabelElement v => Text(v.X, v.Y, v.W, v.H, v.Font),
                LogoElement g => new[] { g.X, g.Y, g.W, g.W * 0.3 },
                BarElementDef b => new[]
                {
                    b.X, b.Y, b.W.IsFill ? def.Space.W - 2 * b.X : b.W.Value, b.H,
                },
                IconRowElement r => Row(r),
                SpriteElement s => new[] { s.X, s.Y, s.W, s.H },
                LogColumnElement lc => new[] { lc.X, lc.Y, 240.0, lc.Lines * lc.LineH },
                LogRowsElement lr => new[] { lr.X, lr.Y, lr.W, lr.Lines * lr.LineH },
                _ => null,
            };
            if (rect != null) outBoxes.Add(new Box(e.Id, rect[0], rect[1], rect[2], rect[3]));
        }
        return outBoxes;
    }

    private static double[]? FirstRowRect(MaskTrackElement m, Dictionary<string, IconRowElement> rows)
    {
        foreach (var member in m.Members)
            if (rows.TryGetValue(member, out var r)) return Row(r);
        return null;
    }

    private static double[] Text(double x, double y, double? w, double? h, double font) =>
        new[] { x, y, w ?? 240.0, h ?? font * 1.4 + 6.0 };

    private static double[] Row(IconRowElement r)
    {
        double span = r.Count * r.Size + (r.Count - 1) * r.Gap;
        double top = (r.Bottom ?? (r.Cy ?? 0) + r.Size / 2) - r.Size;
        return new[] { r.Cx - span / 2, top, span, r.Size };
    }
}
