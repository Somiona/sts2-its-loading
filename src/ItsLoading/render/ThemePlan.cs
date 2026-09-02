using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

#nullable enable

namespace ItsLoading;

/// <summary>主题单位中的确定矩形；ThemeCompiler 之后 renderer 不再重复布局数学。</summary>
internal readonly record struct ThemeRect(double X, double Y, double W, double H);

internal sealed record IconRowPlan(
    IconRowElement Element,
    ThemeRect Bounds,
    IReadOnlyList<ThemeRect> Slots,
    IReadOnlyList<string> Sources);

internal sealed record DotsPlan(
    DotsElement Element,
    IReadOnlyList<ThemeRect> Dots);

internal sealed record MaskTrackPlan(
    MaskTrackElement Element,
    ThemeRect Domain);

/// <summary>
/// ThemeSpec(ThemeDef)编译后的 C# 渲染计划：默认值、资源序列和跨元素几何均已确定。
/// native adapter 只负责把计划映射到具体图形对象。
/// </summary>
internal sealed record ThemePlan
{
    public ThemeSpaceDef Space { get; init; } = ThemeSpaceDef.Screen;
    public IReadOnlyList<ThemeElementDef> Elements { get; init; } = Array.Empty<ThemeElementDef>();
    public IReadOnlyDictionary<string, IconRowPlan> Rows { get; init; }
        = new Dictionary<string, IconRowPlan>();
    public IReadOnlyDictionary<string, DotsPlan> DotSets { get; init; }
        = new Dictionary<string, DotsPlan>();
    public IReadOnlyDictionary<string, MaskTrackPlan> Masks { get; init; }
        = new Dictionary<string, MaskTrackPlan>();
    public IReadOnlyList<string> NativeIncompatibilities { get; init; } = Array.Empty<string>();
    public bool SupportsNative => NativeIncompatibilities.Count == 0;
}

/// <summary>theme.json → ThemePlan 的唯一 C# 语义入口。</summary>
internal static class ThemeCompiler
{
    internal static ThemePlan? Compile(string themeDir, Action<string>? warn = null)
    {
        warn ??= _ => { };
        ThemeDef? spec = ThemeDef.Load(themeDir, warn);
        if (spec == null) return null;

        ThemeElementDef[] elements = spec.Elements.Select(Normalize).ToArray();
        var rows = new Dictionary<string, IconRowPlan>();
        var dots = new Dictionary<string, DotsPlan>();
        var masks = new Dictionary<string, MaskTrackPlan>();
        var nativeIncompatibilities = new List<string>();

        foreach (ThemeElementDef element in elements)
        {
            if (element is LogoElement logo) WarnMissing(themeDir, logo.Id, logo.Src, warn);
            if (element is SpriteElement sprite) WarnMissing(themeDir, sprite.Id, sprite.Src, warn);
            switch (element)
            {
                case IconRowElement row:
                {
                    double span = row.Count * row.Size + (row.Count - 1) * row.Gap;
                    double x0 = row.Cx - span / 2;
                    double bottom = row.Bottom ?? (row.Cy ?? 0) + row.Size / 2;
                    double y = bottom - row.Size;
                    var slots = new ThemeRect[row.Count];
                    var sources = new string[row.Count];
                    for (int i = 0; i < row.Count; i++)
                    {
                        slots[i] = new ThemeRect(x0 + i * (row.Size + row.Gap), y,
                            row.Size, row.Size);
                        sources[i] = Source(row, i);
                        WarnMissing(themeDir, row.Id, sources[i], warn);
                    }
                    rows[row.Id] = new IconRowPlan(row,
                        new ThemeRect(x0, y, span, row.Size), slots, sources);
                    break;
                }
                case DotsElement dot when rows.TryGetValue(dot.Of, out IconRowPlan? rowPlan):
                {
                    IconRowElement row = rowPlan.Element;
                    double size = row.Size * dot.Scale;
                    var rects = new ThemeRect[Math.Max(0, row.Count - 1)];
                    for (int i = 0; i < rects.Length; i++)
                    {
                        double cx = rowPlan.Bounds.X + (i + 1) * row.Size
                            + i * row.Gap + row.Gap / 2;
                        rects[i] = new ThemeRect(cx - size / 2, dot.Cy - size / 2, size, size);
                    }
                    dots[dot.Id] = new DotsPlan(dot, rects);
                    break;
                }
                case MaskTrackElement mask:
                {
                    IconRowPlan? domain = mask.Members
                        .Select(id => rows.TryGetValue(id, out IconRowPlan? row) ? row : null)
                        .FirstOrDefault(row => row != null);
                    if (domain != null)
                        masks[mask.Id] = new MaskTrackPlan(mask, domain.Bounds);
                    if (!NearlyEqual(mask.Tint.R, mask.Tint.G) || !NearlyEqual(mask.Tint.G, mask.Tint.B))
                        nativeIncompatibilities.Add($"{mask.Id}: native mask 目前只支持灰阶 tint");
                    break;
                }
            }
            if (element is LogElementDef log && log.Overrun.HasValue)
                nativeIncompatibilities.Add($"{log.Id}: native 目前不支持显式 overrun");
        }

        return new ThemePlan
        {
            Space = spec.Space,
            Elements = elements,
            Rows = rows,
            DotSets = dots,
            Masks = masks,
            NativeIncompatibilities = nativeIncompatibilities,
        };
    }

    private static ThemeElementDef Normalize(ThemeElementDef element) => element switch
    {
        LogRowsElement log => log with { Align = log.Align ?? 1 },
        IconRowElement row => row with { Pivot = row.Pivot ?? "center" },
        _ => element,
    };

    private static string Source(IconRowElement row, int slot)
    {
        if (row.Src != null) return row.Src;
        string index = (row.IndexBase + slot).ToString(CultureInfo.InvariantCulture);
        return (row.Pattern ?? "%d").Replace("%d", index, StringComparison.Ordinal);
    }

    private static void WarnMissing(string themeDir, string id, string source, Action<string> warn)
    {
        if (!File.Exists(Path.Combine(themeDir, source)))
            warn($"[theme] {id}: 素材不存在 '{source}' — 使用声明的降级表现");
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.0001;
}
