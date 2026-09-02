using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using ItsLoading;
using Xunit;

// 启动时间线数学的离线回归:
// mod 段公式、步骤分数与相邻差分、会话插值与空会话、锚点兜底。

internal sealed class Spy : List<LoadingViewState>
{
    public Action<LoadingViewState> AsPresenter() => Add;

    public LoadingViewState Last => this[^1];
}

internal sealed class ScriptedClock
{
    public long EngineMsec;
    public long Ticks;

    internal BootTimeline MakeTimeline(Spy spy)
    {
        var timeline = new BootTimeline(() => EngineMsec, () => Ticks);
        timeline.Connect(spy.AsPresenter());
        return timeline;
    }

    /// <summary>毫秒 → Stopwatch ticks(按本机 Frequency,与生产同一换算)。</summary>
    public long MsToTicks(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    public void AdvanceMs(double ms)
    {
        EngineMsec += (long)ms;
        Ticks += MsToTicks(ms);
    }
}

public class BootTimelineTests
{
    private static Func<int, string> Text(string prefix) => n => $"{prefix}{n}";

    [Fact]
    public void Presenter_connection_is_single_assignment()
    {
        var timeline = new BootTimeline(() => 0, () => 0);
        timeline.Connect(_ => { });
        Assert.Throws<InvalidOperationException>(() => timeline.Connect(_ => { }));
    }

    [Fact]
    public void BeginMods_presents_overall_and_local_progress()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        clock.MakeTimeline(spy).BeginMods(8, 1, "Loading mods 1/8");

        var p = Assert.Single(spy);
        Assert.Equal(0.25f + 0.35f / 8f, p.Overall, 5);
        Assert.Equal(1f / 8f, p.Local);
        Assert.Equal(BootStage.Mods, p.Stage);
        Assert.Equal("Loading mods 1/8", p.Step);
        Assert.Equal("", p.Detail);
        Assert.False(p.ForceDraw);
    }

    [Fact]
    public void BeginMods_clamps_total_to_one()
    {
        var clock = new ScriptedClock();
        var tl = clock.MakeTimeline(new Spy());
        tl.BeginMods(0, 1, "x");
        Assert.Equal(1, tl.Total);
    }

    [Fact]
    public void BeginMods_accounts_for_mods_processed_before_itsloading()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);

        tl.BeginMods(7, 5, "Loading mods 5/7");

        Assert.Equal(5, tl.Count);
        Assert.Equal(5f / 7f, spy.Last.Local, 5);
        Assert.Equal(0.25f + 0.35f * (5f / 7f), spy.Last.Overall, 5);
    }

    [Fact]
    public void ModStarted_presents_current_mod_before_expensive_work()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(4, 1, "mods");

        tl.ModStarted("mods", "A", "A");

        Assert.Equal(0.25f + 0.35f / 4f, spy.Last.Overall, 5);
        Assert.Equal(0.25f, spy.Last.Local);
        Assert.Equal("mods", spy.Last.Step);
        Assert.Equal("A", spy.Last.Detail);
        Assert.True(spy.Last.ForceDraw);
        Assert.Equal(1, tl.PrefixCalls);
    }

    [Fact]
    public void ModLoaded_advances_formula_and_completes_at_sixty_percent()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(4, 1, "mods"); // 自身=1,再装 3 个到 4

        tl.ModStarted("mods", "A", "A");
        Assert.Equal(2, tl.ModLoaded("A", "Loaded", Text("A-"), "dA", "done", n => $"fin{n}"));
        Assert.Equal(0.25f + 0.35f * (2 / 4f), spy.Last.Overall, 5); // 0.425
        Assert.Equal(0.5f, spy.Last.Local);
        Assert.Equal("A-2", spy.Last.Step); // 计数文案由时间线注入 n
        Assert.Equal("dA", spy.Last.Detail);
        Assert.False(spy.Last.ForceDraw);

        tl.ModStarted("mods", "B", "B");
        Assert.Equal(3, tl.ModLoaded("B", "Loaded", Text("B-"), "dB", "done", n => $"fin{n}"));
        Assert.Equal(0.25f + 0.35f * (3 / 4f), spy.Last.Overall, 5);

        tl.ModStarted("mods", "C", "C");
        Assert.Equal(4, tl.ModLoaded("C", "Loaded", Text("C-"), "dC", "done", n => $"fin{n}"));
        Assert.True(tl.ModsDone);
        Assert.Equal(0.60f, spy.Last.Overall);            // 完成呈现
        Assert.Equal(1f, spy.Last.Local);
        Assert.Equal("done", spy.Last.Step);
        Assert.Equal("fin4", spy.Last.Detail);          // 完成文案带计数

        var phase = Assert.Single(tl.PhaseSpans);
        Assert.Equal("phase.mod_load", phase.Id);
        Assert.Equal("4 mods", phase.Detail);
    }

    [Fact]
    public void ModLoaded_records_span_on_engine_timeline()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var tl = clock.MakeTimeline(new Spy());
        tl.BeginMods(2, 1, "m");
        clock.Ticks = clock.MsToTicks(1_000);           // prefix 时刻(ticks 1000ms)
        tl.ModStarted("m", "A", "A");
        clock.Ticks = clock.MsToTicks(1_300);           // postfix 时刻
        tl.ModLoaded("A", "Loaded", Text("x"), "d", "done", n => "f");

        var span = Assert.Single(tl.ModSpans);
        Assert.Equal("A", span.Id);
        Assert.Equal("Loaded", span.Detail);
        Assert.Equal(300.0, span.DurationMs, 1);        // prefix→postfix 真实区间
        // 引擎毫秒起点 = 偏移(构造时对齐于 ticks=0 ↔ engine=10000) + 1000ms
        Assert.Equal(11_000.0, span.StartMs, 1);
    }

    [Fact]
    public void StepStarted_uses_fraction_table_and_closes_previous_span_by_differencing()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);

        clock.Ticks = clock.MsToTicks(2_000);
        tl.StepStarted("step.atlas", "图集加载", "+2000ms");
        Assert.Equal(0.600f, spy.Last.Overall);
        Assert.Equal(0f, spy.Last.Local);
        var first = Assert.Single(tl.StepSpans);
        Assert.Equal(0, first.DurationMs);              // 开启时未知时长

        clock.Ticks = clock.MsToTicks(2_050);
        tl.StepStarted("step.loc", "本地化初始化", "+2050ms");
        Assert.Equal(0.615f, spy.Last.Overall);
        Assert.Equal(0.25f, spy.Last.Local);
        Assert.Equal(2, tl.StepSpans.Count);
        Assert.Equal(50.0, tl.StepSpans[0].DurationMs, 1); // 相邻差分收尾
        Assert.Equal(0, tl.StepSpans[1].DurationMs);
    }

    [Fact]
    public void StepStarted_ignores_unknown_keys()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(2, 1, "m");

        tl.StepStarted("step.nope", "x", "y");

        Assert.Single(spy);                              // 只有 BeginMods 那次
        Assert.Empty(tl.StepSpans);
    }

    [Fact]
    public void SetEssentialSteps_evenly_rescales_installed_steps()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        // beta 全量八步(asmInfo 在内)
        tl.SetEssentialSteps(new[]
        {
            "step.atlas", "step.loc", "step.asmInfo", "step.modeldb",
            "step.modelIdCache", "step.ids", "step.msgTypes", "step.actTypes",
        });

        tl.StepStarted("step.loc", "loc", "");
        Assert.Equal(0.60f + 0.06f / 8f, spy.Last.Overall, 5);
        Assert.Equal(1f / 8f, spy.Last.Local, 5);

        tl.StepStarted("step.actTypes", "act", "");
        Assert.Equal(0.60f + 0.06f * 7f / 8f, spy.Last.Overall, 5);
        Assert.Equal(7f / 8f, spy.Last.Local, 5);

        // 传入乱序时按执行序重排;未安装的键照旧忽略
        var tl2 = clock.MakeTimeline(new Spy());
        tl2.SetEssentialSteps(new[] { "step.ids", "step.atlas" });
        tl2.StepStarted("step.atlas", "atlas", "");
        Assert.Equal(0, tl2.Current.Local, 5);           // i=0 → 局部 0
        tl2.StepStarted("step.ids", "ids", "");
        Assert.Equal(0.5f, tl2.Current.Local, 5);        // i=1/2
        tl2.StepStarted("step.msgTypes", "x", "");       // 未安装 → 忽略
    }

    [Fact]
    public void EssentialCompleted_closes_last_step_at_essential_boundary()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        clock.Ticks = clock.MsToTicks(2_000);
        tl.StepStarted("step.ids", "ids", "");

        clock.Ticks = clock.MsToTicks(2_040);
        tl.EssentialCompleted();

        Assert.Equal(40.0, Assert.Single(tl.StepSpans).DurationMs, 1);
        Assert.Equal(0.66f, spy.Last.Overall);
        Assert.Equal(1f, spy.Last.Local);
        Assert.Equal(BootStage.Essential, spy.Last.Stage);
    }

    [Fact]
    public void SessionAdvanced_interpolates_range_and_records_completion()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        var session = new object();

        Assert.True(tl.TracksSession("IntroLogo"));
        clock.Ticks = clock.MsToTicks(3_000);
        tl.SessionAdvanced(session, "IntroLogo", 5, 5, "logo.png", "assets", (t, item) => $"{5}/{t} · {item}");
        Assert.Equal(0.66f + (0.70f - 0.66f) * 0.5f, spy.Last.Overall, 5); // 中点 0.68
        Assert.Equal(0.5f, spy.Last.Local, 5);
        Assert.Equal("5/10 · logo.png", spy.Last.Detail);
        Assert.False(spy.Last.ForceDraw);                    // 会话事件不强制出帧

        clock.Ticks = clock.MsToTicks(3_400);
        tl.SessionAdvanced(session, "IntroLogo", 10, 0, null, "assets", (t, item) => $"{10}/{t} · {item}");
        Assert.Equal(0.70f, spy.Last.Overall, 5);
        var span = Assert.Single(tl.SessionSpans);
        Assert.Equal("IntroLogo", span.Id);
        Assert.Equal(400.0, span.DurationMs, 1);
        Assert.Equal("10/10", span.Detail);
    }

    [Fact]
    public void SessionAdvanced_empty_session_is_complete_not_NaN()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(2, 1, "m");

        tl.SessionAdvanced(new object(), "IntroLogo", 0, 0, null, "a", (t, item) => $"0/{t}");

        Assert.Equal(0.70f, spy.Last.Overall, 5);           // 0/0 按已完成,不产生 NaN
        Assert.Empty(tl.SessionSpans);                   // Total=0 不记 span
    }

    [Fact]
    public void SessionAdvanced_ignores_untracked_sessions_and_postfreeze_events()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(2, 1, "m");
        int before = spy.Count;

        Assert.False(tl.TracksSession("SomeRoomLoad"));  // 游戏内会话:钩子连反射读都省了
        tl.SessionAdvanced(new object(), "SomeRoomLoad", 1, 1, null, "a", (t, item) => "x");
        Assert.Equal(before, spy.Count);

        tl.MenuReady("done", "123ms");
        Assert.False(tl.TracksSession("IntroLogo"));     // 冻结后
        tl.SessionAdvanced(new object(), "IntroLogo", 1, 0, null, "a", (t, item) => "x");
        Assert.Equal(before + 1, spy.Count);             // 只有 MenuReady 那次
    }

    [Fact]
    public void Freeze_rejects_late_steps_and_waypoints()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.MenuReady("done", "x");
        int before = spy.Count;

        tl.StepStarted("step.atlas", "late", "late");
        tl.Waypoint(BootWaypoint.MenuLoad, "late", "late");

        Assert.Equal(before, spy.Count);
        Assert.Empty(tl.StepSpans);
        Assert.Equal(1f, tl.Current.Overall);
    }

    [Fact]
    public void SetBootAnchor_records_prelude_and_engine_init_spans()
    {
        var clock = new ScriptedClock { EngineMsec = 5_000 };
        var tl = clock.MakeTimeline(new Spy());

        tl.SetBootAnchor(1_000);

        Assert.Equal(2, tl.PhaseSpans.Count);
        Assert.Equal("phase.prelude", tl.PhaseSpans[0].Id);
        Assert.Equal(1_000.0, tl.PhaseSpans[0].StartMs, 1);
        Assert.Equal(4_000.0, tl.PhaseSpans[0].DurationMs, 1);
        Assert.Equal("phase.engine_init", tl.PhaseSpans[1].Id);
        Assert.Equal(0.0, tl.PhaseSpans[1].StartMs, 1);
        Assert.Equal(1_000.0, tl.PhaseSpans[1].DurationMs, 1);
    }

    [Fact]
    public void MenuReady_without_anchor_falls_back_to_engine_now()
    {
        var clock = new ScriptedClock { EngineMsec = 9_123 };
        var tl = clock.MakeTimeline(new Spy());

        tl.MenuReady("done", "x");

        Assert.Equal(9_123.0, tl.TotalBootMs, 1);        // 无锚点:用引擎当前时刻
        Assert.True(tl.Frozen);
    }

    [Fact]
    public void MenuReady_with_anchor_measures_from_boot()
    {
        var clock = new ScriptedClock { EngineMsec = 12_000 };
        var tl = clock.MakeTimeline(new Spy());
        tl.SetBootAnchor(2_000);

        tl.MenuReady("done", "x");

        Assert.Equal(10_000.0, tl.TotalBootMs, 1);
    }

    [Fact]
    public void MenuReady_closes_pending_step_span_and_presents_one()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        clock.Ticks = clock.MsToTicks(2_000);
        tl.StepStarted("step.atlas", "a", "d");

        clock.Ticks = clock.MsToTicks(2_080);
        tl.MenuReady("Ready", "80ms");

        Assert.Equal(1.0f, spy.Last.Overall);
        Assert.Equal("Ready", spy.Last.Step);
        Assert.Equal(80.0, tl.StepSpans[0].DurationMs, 1); // 末步骤收尾
    }

    [Fact]
    public void Waypoint_dedups_main_menu_and_uses_table_fractions()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);

        tl.Waypoint(BootWaypoint.MainMenu, "opening", "+1ms");
        tl.Waypoint(BootWaypoint.MainMenu, "opening", "+2ms"); // 二次被去重
        Assert.Equal(0.66f, spy.Last.Overall);
        tl.Waypoint(BootWaypoint.Logo, "logo", "");
        Assert.Equal(0.82f, spy.Last.Overall);
        tl.Waypoint(BootWaypoint.MenuLoad, "menu", "");
        Assert.Equal(0.88f, spy.Last.Overall);

        Assert.Equal(3, spy.Count);
        Assert.Equal(BootStage.Menu, spy.Last.Stage);
    }

    [Fact]
    public void BootSummary_lists_slowest_mods_and_null_when_empty()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var tl = clock.MakeTimeline(new Spy());
        Assert.Null(tl.BootSummary());

        tl.BeginMods(3, 1, "m");
        clock.Ticks = clock.MsToTicks(100);
        tl.ModStarted("m", "Slow", "Slow");
        clock.Ticks = clock.MsToTicks(400);
        tl.ModLoaded("Slow", "Loaded", Text("x"), "d", "done", n => "f");
        clock.Ticks = clock.MsToTicks(500);
        tl.ModStarted("m", "Fast", "Fast");
        clock.Ticks = clock.MsToTicks(510);
        tl.ModLoaded("Fast", "Loaded", Text("x"), "d", "done", n => "f");

        string s = tl.BootSummary();
        Assert.StartsWith("[ItsLoading] boot ", s);
        Assert.Contains("slowest mods:", s);
        Assert.Contains("Slow=300ms Fast=10ms", s);
        Assert.Contains("prefix=2 postfix=2", s);
    }

    [Fact]
    public void Activity_only_changes_detail_no_progress()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(4, 1, "mods 1/4");
        tl.ModStarted("mods 1/4", "mod-a", "mod-a");
        int before = spy.Count;
        float overall = spy.Last.Overall, local = spy.Last.Local;
        var stage = spy.Last.Stage;

        tl.Activity("sub-step of mod-a");

        Assert.Equal(before + 1, spy.Count);
        Assert.Equal(overall, spy.Last.Overall);   // 不推进进度条
        Assert.Equal(local, spy.Last.Local);
        Assert.Equal(stage, spy.Last.Stage);
        Assert.Equal("sub-step of mod-a", spy.Last.Detail);
        Assert.False(spy.Last.ForceDraw);
    }

    [Fact]
    public void Activity_is_ignored_after_freeze()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(2, 1, "mods");
        tl.MenuReady("done", "10ms");

        tl.Activity("late");

        Assert.DoesNotContain(spy, p => p.Detail == "late");
    }

    [Fact]
    public void ModSubStep_records_span_under_current_mod_without_progress()
    {
        var clock = new ScriptedClock();
        var spy = new Spy();
        var tl = clock.MakeTimeline(spy);
        tl.BeginMods(4, 1, "mods");
        tl.ModStarted("mods", "heavy-mod", "heavy-mod");
        float overall = spy.Last.Overall, local = spy.Last.Local;

        long start = clock.Ticks;
        clock.AdvanceMs(1200);
        tl.ModSubStep("init Loader", start, clock.Ticks);

        var span = Assert.Single(tl.SubStepSpans);
        Assert.Equal("heavy-mod", span.Id);
        Assert.Equal(ItsLoading.Api.LoadPhase.ModSubStep, span.Phase);
        Assert.Equal("init Loader", span.Detail);
        Assert.Equal(1200.0, span.DurationMs, 1);          // 引擎时间轴上的真实时长
        Assert.Equal(overall, spy.Last.Overall);            // 不推进进度条
        Assert.Equal(local, spy.Last.Local);

        tl.MenuReady("done", "0ms");
        tl.ModSubStep("late", clock.Ticks, clock.Ticks);
        Assert.Single(tl.SubStepSpans);                     // 冻结后拒写
    }

    [Fact]
    public void RecordWorkshopScan_spans_by_observation_gaps()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var tl = clock.MakeTimeline(new Spy());

        tl.RecordWorkshopScan(new()
        {
            ("3747515571", "Remilia", 10_500.0),
            ("3747526116", "Watcher", 10_720.0),
            ("3747508952", "figure_Saya", 11_000.0),
        }, endMs: 11_400.0);

        Assert.Equal(3, tl.WorkshopSpans.Count);
        Assert.Equal(220.0, tl.WorkshopSpans[0].DurationMs, 1);   // 相邻观测差分
        Assert.Equal("Remilia", tl.WorkshopSpans[0].Detail);
        Assert.Equal(ItsLoading.Api.LoadPhase.Prelude, tl.WorkshopSpans[0].Phase);
        Assert.Equal(400.0, tl.WorkshopSpans[2].DurationMs, 1);   // 末项以 endMs 收尾

        tl.MenuReady("done", "0ms");
        tl.RecordWorkshopScan(new() { ("x", "", 1.0) }, 2.0);
        Assert.Equal(3, tl.WorkshopSpans.Count);                  // 冻结后拒写
    }

    [Fact]
    public void Waypoints_record_transition_segments_between_boundaries()
    {
        var clock = new ScriptedClock();
        var tl = clock.MakeTimeline(new Spy());

        tl.EssentialCompleted();                                  // 开 wp.cloudSave
        clock.AdvanceMs(1400);
        tl.Waypoint(BootWaypoint.MainMenu, "s", "d");             // 关 cloudSave,开 preLogo
        clock.AdvanceMs(200);
        tl.Waypoint(BootWaypoint.Logo, "s", "d");                 // 关 preLogo,开 logo
        clock.AdvanceMs(6600);
        tl.Waypoint(BootWaypoint.MenuLoad, "s", "d");             // 关 logo,开 menuScene
        clock.AdvanceMs(800);
        tl.MenuReady("done", "d");                                // 关 menuScene

        Assert.Equal(new[] { "wp.cloudSave", "wp.preLogo", "wp.logo", "wp.menuScene" },
            tl.WaypointSpans.Select(s => s.Id));
        Assert.All(tl.WaypointSpans, s => Assert.Equal(ItsLoading.Api.LoadPhase.Transition, s.Phase));
        Assert.Equal(1400.0, tl.WaypointSpans[0].DurationMs, 1);
        Assert.Equal(200.0, tl.WaypointSpans[1].DurationMs, 1);
        Assert.Equal(6600.0, tl.WaypointSpans[2].DurationMs, 1);
        Assert.Equal(800.0, tl.WaypointSpans[3].DurationMs, 1);
        // 相邻段首尾相接
        Assert.Equal(tl.WaypointSpans[0].StartMs + tl.WaypointSpans[0].DurationMs,
            tl.WaypointSpans[1].StartMs, 1);
    }

    [Fact]
    public void PostMods_boundary_opens_at_last_mod_and_closes_at_first_step()
    {
        var clock = new ScriptedClock { EngineMsec = 10_000 };
        var tl = clock.MakeTimeline(new Spy());
        tl.BeginMods(2, 1, "m");
        tl.ModStarted("m", "A", "A");
        clock.AdvanceMs(400);
        tl.ModLoaded("A", "Loaded", Text("x"), "d", "done", n => "f");   // ModsDone → 开 wp.postMods
        clock.AdvanceMs(300);
        tl.StepStarted("step.atlas", "atlas", "");                       // 关 wp.postMods
        clock.AdvanceMs(50);
        tl.StepStarted("step.loc", "loc", "");                           // 无开段,不新增

        var span = Assert.Single(tl.WaypointSpans);
        Assert.Equal("wp.postMods", span.Id);
        Assert.Equal(300.0, span.DurationMs, 1);
    }
}
