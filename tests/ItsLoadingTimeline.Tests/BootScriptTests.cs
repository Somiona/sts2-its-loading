using System;
using System.Collections.Generic;
using System.Linq;
using ItsLoading;
using Xunit;

#nullable enable

// 画廊预览伪启动剧本回归(纯 BCL):
//   完整一轮 —— 进度单调跑到 100%,阶段 1..7 依序推进,末段 Ready 停顿;
//   循环 —— 回卷后状态归零,像一次全新启动;
//   重放 —— 单次 Tick(大 t) 与连续播放到同一时刻的状态完全一致
//   (静态缩略图与 hover 实况同源的保证)。

public sealed class BootScriptTests
{
    private static BootScript Script() => new((key, _) => key); // 身份翻译:断言只看结构

    [Fact]
    public void Runs_to_full_progress_then_holds_ready()
    {
        var script = Script();
        // 末段(Ready 停顿)内:overall 满、阶段 7、local 满
        var ready = script.Tick(script.Total - 0.5);
        Assert.Equal(1.0, ready.Overall, 5);
        Assert.Equal(BootScript.StageCount, ready.Stage);
        Assert.Equal(1.0f, ready.Local, 5);
    }

    [Fact]
    public void Overall_is_monotonic_within_a_run_across_two_cycles()
    {
        var script = Script();
        double prevOverall = 0;
        double prevT = 0;
        bool sawFull = false;
        for (double t = 1.0 / 120.0; t <= script.Total * 2 + 1; t += 1.0 / 120.0)
        {
            var f = script.Tick(t);
            if (f.T < prevT) prevOverall = 0; // 回卷 = 新一轮,进度从头
            Assert.True(f.Overall >= prevOverall - 1e-9,
                $"overall 回退 @{f.T}s: {prevOverall} → {f.Overall}");
            sawFull |= f.Overall >= 1.0 - 1e-9;
            prevOverall = f.Overall;
            prevT = f.T;
        }
        Assert.True(sawFull, "两轮内 overall 应至少一次到达 100%");
    }

    [Fact]
    public void Stages_advance_one_to_seven_in_order()
    {
        var script = Script();
        var stages = new List<int>();
        // 恰好一轮(不到回卷):阶段应严格依序 1..7
        for (double t = 1.0 / 120.0; t < script.Total - 0.01; t += 1.0 / 120.0)
        {
            var f = script.Tick(t);
            if (stages.Count == 0 || stages[^1] != f.Stage) stages.Add(f.Stage);
        }
        Assert.Equal(Enumerable.Range(1, BootScript.StageCount).ToList(), stages);
    }

    [Fact]
    public void Wrap_resets_state_like_a_fresh_boot()
    {
        var script = Script();
        var end = script.Tick(script.Total - 0.1); // Ready 停顿末尾:日志非空、阶段 7
        Assert.NotEmpty(end.Log);
        Assert.Equal(BootScript.StageCount, end.Stage);

        var fresh = script.Tick(script.Total + 0.2); // 回卷后 0.2s
        Assert.Equal(1, fresh.Stage);
        Assert.True(fresh.StageChanged, "回卷后首帧应再次触发 stage_changed");
        Assert.True(fresh.Overall < 0.1);
        Assert.Empty(fresh.Log);
    }

    [Fact]
    public void Single_replay_matches_continuous_playback()
    {
        var continuous = Script();
        BootScript.Snapshot at = default;
        for (double t = 1.0 / 60.0; t <= BootScript.SnapshotAt; t += 1.0 / 60.0)
            at = continuous.Tick(t);
        var replayed = Script().Tick(BootScript.SnapshotAt); // 静态缩略图路径

        Assert.Equal(at.Overall, replayed.Overall, 9);
        Assert.Equal(at.Stage, replayed.Stage);
        Assert.Equal(at.Step, replayed.Step);
        Assert.Equal(at.Detail, replayed.Detail);
        Assert.Equal(at.Log, replayed.Log);
        Assert.NotEmpty(replayed.Log); // 取景点在 mod 加载段:日志确实滚起来了
    }

    [Fact]
    public void Snapshot_lands_mid_mod_loading()
    {
        var s = Script().Tick(BootScript.SnapshotAt);
        Assert.Equal(3, s.Stage);                 // mod 加载 = 第 3 阶段
        Assert.InRange(s.Overall, 0.4, 0.7);      // 进度过半附近
        Assert.False(s.Indeterminate);
    }

    [Fact]
    public void Preview_pauses_resumes_and_keeps_looping()
    {
        var script = Script();
        var initial = script.Seek(BootScript.SnapshotAt);

        script.Playing = false;
        var paused = script.Advance(10);
        Assert.Equal(initial, paused);

        script.Playing = true;
        var resumed = script.Advance(0.25);
        Assert.Equal(initial.T + 0.25, resumed.T, 5);

        var wrapped = script.Advance(script.Total);
        Assert.Equal(resumed.T, wrapped.T, 5);
    }
}
