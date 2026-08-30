using System;
using System.Collections.Generic;
using System.Linq;
using ItsLoading;
using Api = ItsLoading.Api;
using Xunit;

// 瀑布图行树的离线回归:
// 挂接规则(工坊项/mod 按时间窗包含、子步骤按 Id)、无父落回根行、
// 折叠拍平与展开、缝隙填补阈值。

public class WaterfallTreeTests
{
    private static Api.LoadSpan Span(string id, Api.LoadPhase phase, double start, double dur,
        string detail = "") => new(id, phase, start, dur, detail);

    [Fact]
    public void Workshop_items_nest_under_prelude_when_contained()
    {
        var phases = new[]
        {
            Span("phase.engine_init", Api.LoadPhase.Prelude, 0, 800),
            Span("phase.prelude", Api.LoadPhase.Prelude, 800, 4000),
        };
        var workshop = new[]
        {
            Span("workshop 111", Api.LoadPhase.Prelude, 900, 500, "ModA"),
            Span("workshop 222", Api.LoadPhase.Prelude, 1500, 300, "ModB"),
        };

        var roots = WaterfallViewer.BuildRowTree(
            phases, Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(),
            Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), workshop);

        var prelude = roots.Single(r => r.Span.Id == "phase.prelude");
        Assert.Equal(new[] { "workshop 111", "workshop 222" },
            prelude.Children.Select(c => c.Span.Id));
        // 其余根行不含工坊项
        Assert.Equal(2, roots.Count);
    }

    [Fact]
    public void Workshop_item_outside_prelude_window_falls_back_to_root()
    {
        var phases = new[] { Span("phase.prelude", Api.LoadPhase.Prelude, 800, 1000) };
        // 结束点超出 prelude 窗口远超容差(轮询量化 eps=250ms)
        var workshop = new[] { Span("workshop 333", Api.LoadPhase.Prelude, 2400, 900) };

        var roots = WaterfallViewer.BuildRowTree(
            phases, Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(),
            Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), workshop);

        Assert.Empty(roots.Single(r => r.Span.Id == "phase.prelude").Children);
        Assert.Contains(roots, r => r.Span.Id == "workshop 333");
    }

    [Fact]
    public void Empty_workshop_leaves_prelude_without_children()
    {
        // 扫描期主循环不迭代的启动形态:无逐项观测,prelude 按聚合显示、无展开符
        var phases = new[] { Span("phase.prelude", Api.LoadPhase.Prelude, 800, 4000) };

        var roots = WaterfallViewer.BuildRowTree(
            phases, Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(),
            Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(),
            Array.Empty<Api.LoadSpan>());

        Assert.Empty(roots.Single(r => r.Span.Id == "phase.prelude").Children);
    }

    [Fact]
    public void Mods_nest_under_mod_phase_and_substeps_attach_by_id()
    {
        var phases = new[] { Span("phase.mod_load", Api.LoadPhase.ModLoad, 5000, 3000) };
        var mods = new[]
        {
            Span("BaseLib", Api.LoadPhase.ModLoad, 5200, 800, "Loaded"),
            Span("BetterModMenu", Api.LoadPhase.ModLoad, 6100, 900, "Loaded"),
            Span("Stray", Api.LoadPhase.ModLoad, 9500, 400, "Loaded"), // 窗口外 → 根行
        };
        var subs = new[]
        {
            Span("BaseLib", Api.LoadPhase.ModSubStep, 5210, 30, "pck baselib.pck"),
            Span("BaseLib", Api.LoadPhase.ModSubStep, 5250, 700, "init MainFile"),
            Span("Ghost", Api.LoadPhase.ModSubStep, 10, 5, "orphan"), // 无父 → 根行
        };

        var roots = WaterfallViewer.BuildRowTree(
            phases, Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(),
            mods, subs, Array.Empty<Api.LoadSpan>());

        var phase = roots.Single(r => r.Span.Id == "phase.mod_load");
        Assert.Equal(2, phase.Children.Count);
        var baseLib = phase.Children.Single(c => c.Span.Id == "BaseLib");
        Assert.Equal(new[] { "pck baselib.pck", "init MainFile" },
            baseLib.Children.Select(c => c.Span.Detail));
        Assert.Contains(roots, r => r.Span.Id == "Stray");
        Assert.Contains(roots, r => r.Span.Id == "Ghost");
    }

    [Fact]
    public void Flatten_hides_children_until_parent_expanded()
    {
        var phases = new[] { Span("phase.mod_load", Api.LoadPhase.ModLoad, 5000, 3000) };
        var mods = new[] { Span("BaseLib", Api.LoadPhase.ModLoad, 5200, 800) };
        var subs = new[] { Span("BaseLib", Api.LoadPhase.ModSubStep, 5250, 700, "init") };

        var roots = WaterfallViewer.BuildRowTree(
            phases, Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(), Array.Empty<Api.LoadSpan>(),
            mods, subs, Array.Empty<Api.LoadSpan>());

        var collapsed = WaterfallViewer.FlattenRows(roots, new HashSet<string>());
        Assert.DoesNotContain(collapsed, r => r.Span.Phase == Api.LoadPhase.ModSubStep);
        Assert.Equal(0, collapsed.Single(r => r.Span.Id == "phase.mod_load").Depth);

        var expanded = WaterfallViewer.FlattenRows(
            roots, new HashSet<string>
            {
                WaterfallViewer.RowKey(phases[0]), // phase.mod_load(父)
                WaterfallViewer.RowKey(mods[0]),   // BaseLib(祖父级先展开,子行才可见)
            });
        Assert.Equal(0, expanded.Single(r => r.Span.Id == "phase.mod_load").Depth);
        Assert.Equal(1, expanded.Single(r => r.Span.Id == "BaseLib" &&
            r.Span.Phase == Api.LoadPhase.ModLoad).Depth);
        Assert.Equal(2, expanded.Single(r => r.Span.Phase == Api.LoadPhase.ModSubStep).Depth);
    }

    [Fact]
    public void GapFills_reports_only_existing_gaps()
    {
        // preBoot:最早行起点 >100ms 才出现;handoff:prelude 结束与首个窗口后
        // mod 行之间的缝 >20ms 才出现
        var withGaps = new[]
        {
            Span("phase.prelude", Api.LoadPhase.Prelude, 800, 1200),
            Span("BaseLib", Api.LoadPhase.ModLoad, 2100, 400),
        };
        var fills = WaterfallViewer.GapFills(withGaps, "preBoot", "handoff");
        Assert.Equal(2, fills.Count);
        Assert.Equal(("preBoot", 0, 800), (fills[0].Id, fills[0].StartMs, fills[0].DurationMs));
        Assert.Equal(("handoff", 2000, 100), (fills[1].Id, fills[1].StartMs, fills[1].DurationMs));

        // engine_init 从 0 起算 → 无 preBoot;mod 紧贴 prelude → 无 handoff
        var withoutGaps = new[]
        {
            Span("phase.engine_init", Api.LoadPhase.Prelude, 0, 800),
            Span("phase.prelude", Api.LoadPhase.Prelude, 800, 1200),
            Span("BaseLib", Api.LoadPhase.ModLoad, 2005, 400),
        };
        Assert.Empty(WaterfallViewer.GapFills(withoutGaps, "preBoot", "handoff"));
    }
}
