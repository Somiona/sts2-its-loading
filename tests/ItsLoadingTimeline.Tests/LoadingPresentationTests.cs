using System;
using System.Linq;
using ItsLoading;
using Xunit;

#nullable enable

// 唯一加载屏视图模型回归(原 boot.gd/_log_line + FreezeScreen.UpdateLog 双副本
// 的语义,现为单一事实源):
//   阶段包装 —— StepText 经注入的 stageText(生产 = I18n bar.stage 同式)
//   日志环 —— 阶段里程碑 / detail 变化行 / 裸「+ms」带步骤名 / 连续去重 / 上限 60
//   前奏灌入 —— SeedLog 前插,与调用时机无关
public sealed class LoadingPresentationTests
{
    private static LoadingPresentation New(
        Func<int, string, string>? fmt = null) => new(fmt ?? ((s, step) => $"[{s}/7] {step}"));

    private static LoadingViewState State(int stage = 2, string step = "s",
        string detail = "", float overall = 0.5f) =>
        new((BootStage)stage, overall, 0.5f, step, detail, false);

    [Fact]
    public void StepText_is_stage_wrapped()
    {
        var p = New();
        var snap = p.Present(State(stage: 3, step: "[3/14] 加载中"));
        Assert.Equal("[3/7] [3/14] 加载中", snap.StepText);
    }

    [Fact]
    public void Log_records_milestones_details_and_dedupes()
    {
        var p = New();
        p.Present(State(stage: 1, step: "boot"));
        p.Present(State(stage: 1, step: "boot", detail: "mod A +12ms"));
        p.Present(State(stage: 1, step: "boot", detail: "mod A +12ms")); // 重复:不记
        p.Present(State(stage: 1, step: "boot", detail: "mod B +3ms"));
        p.Present(State(stage: 2, step: "essentials"));                  // 阶段里程碑
        Assert.Equal(new[] { "boot", "mod A +12ms", "mod B +3ms", "essentials" },
            p.Present(State(stage: 2, step: "essentials")).Log);
    }

    [Fact]
    public void Log_caps_at_60()
    {
        var p = New();
        p.Present(State(stage: 1, step: "s"));
        for (int i = 0; i < 80; i++) p.Present(State(stage: 1, step: "s", detail: $"line {i}"));
        var log = p.Present(State(stage: 1, step: "s")).Log;
        Assert.Equal(60, log.Count);
        Assert.Equal("line 79", log[^1]);
    }

    [Fact]
    public void SeedLog_prepends_prelude_before_own_lines()
    {
        var p = New();
        p.Present(State(stage: 1, step: "boot")); // 先有自己的里程碑
        p.SeedLog(new[] { "workshop A", "workshop B" }); // 后灌前奏(Handoff 时机不保证)
        var log = p.Present(State(stage: 1, step: "boot")).Log;
        Assert.Equal(new[] { "workshop A", "workshop B", "boot" }, log);
    }

    [Fact]
    public void T_clock_advances_and_stage_change_flag_follows()
    {
        var p = New();
        Assert.True(p.Present(State(stage: 2)).StageChanged);
        Assert.False(p.Present(State(stage: 2)).StageChanged);
        Assert.True(p.Present(State(stage: 3)).StageChanged);
        Assert.True(p.Present(State(stage: 3)).T > 0);
    }
}
