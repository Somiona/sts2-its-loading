using System;
using System.Collections.Generic;
using ItsLoading;
using Xunit;

#nullable enable

// 原生加载屏协调器回归(纯逻辑;帧计数/主线程/环境变量/呈现面全注入假件;
// 视图模型自身在 LoadingPresentationTests):
//   暖场 —— 前 3 次呈现不挂载;冻结确立 —— 第 4 次挂载并转发 + onAttach 一次
//   活跃模式 —— 帧恢复后不拆除,继续转发(原生面拥有像素直到 retire)
//   熔断 —— 呈现异常一次性关闭(拆面 + onBroken 复活 gd);TryAttach 失败同
//   平台缺席 —— 面工厂返回 null = 静默闲置;开关 —— 环境变量
public sealed class FreezeScreenTests
{
    private sealed class FakeSurface : IThemeSurface
    {
        public int Attaches, Presents, Teardowns;
        public double LastOpacity = -1;
        public bool AttachOk = true;
        public bool ThrowOnPresent;
        public readonly List<string> RemoveWhies = new();
        public SurfaceView LastView;

        public bool TryAttach()
        {
            Attaches++;
            return AttachOk;
        }

        public void Present(SurfaceView view)
        {
            if (ThrowOnPresent) throw new InvalidOperationException("boom");
            Presents++;
            LastView = view;
        }

        public void Remove(string why) => RemoveWhies.Add(why);
        public void Teardown() => Teardowns++;
        public void SetOpacity(double opacity) => LastOpacity = opacity;
    }

    private sealed class Rig
    {
        public long Frame = 5;
        public readonly FakeSurface Surface = new();
        public readonly Dictionary<string, string?> Env = new();
        public bool SurfaceAvailable = true;
        public int Attaches, Brokens;
        public readonly FreezeScreen Screen;

        public Rig()
        {
            Screen = new FreezeScreen(
                () => Frame,
                () => true,
                k => Env.TryGetValue(k, out var v) ? v : null,
                () => SurfaceAvailable ? Surface : null,
                _ => { },
                onAttach: () => Attaches++,
                onBroken: () => Brokens++);
        }

        public void Present(float overall = 0.5f, int stage = 2, string step = "s",
            string detail = "")
            => Screen.Present(
                new LoadingViewState((BootStage)stage, overall, 0.5f, step, detail, false),
                Snap(stage, step, detail));
    }

    internal static PresentedSnapshot Snap(int stage, string step, string detail) =>
        new((BootStage)stage, 0.5f, 0.5f, false, 1.0, false, step, detail,
            new[] { step, detail });

    [Fact]
    public void Warmup_presents_do_not_attach()
    {
        var r = new Rig();
        r.Present();
        r.Present();
        r.Present();
        Assert.Equal(0, r.Surface.Attaches);
        Assert.Equal(0, r.Attaches);
    }

    [Fact]
    public void Frozen_frame_attaches_on_fourth_present_and_fires_onAttach_once()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        Assert.Equal(1, r.Surface.Attaches);
        Assert.Equal(1, r.Surface.Presents);
        Assert.Equal(1, r.Attaches); // gd 退场钩子只一次(无双渲染)
        r.Present();
        Assert.Equal(2, r.Surface.Presents);
        Assert.Equal(1, r.Surface.Attaches);
        Assert.Equal(1, r.Attaches);
    }

    [Fact]
    public void Attached_surface_receives_the_shared_snapshot()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present(step: "[3/14] 加载中", detail: "+12ms");
        Assert.Equal("[3/14] 加载中", r.Surface.LastView.Snap.StepText);
        Assert.Equal("+12ms", r.Surface.LastView.Snap.DetailText);
        Assert.Equal(2, r.Surface.LastView.Snap.Log.Count);
    }

    [Fact]
    public void Frame_advance_is_alive_mode_not_removal()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        r.Frame = 6;
        r.Present();
        r.Present();
        Assert.Equal(0, r.Surface.RemoveWhies.Count);
        Assert.Equal(3, r.Surface.Presents);
        Assert.True(r.Screen.IsAlive);
    }

    [Fact]
    public void Healthy_boot_without_freeze_never_attaches()
    {
        var r = new Rig();
        for (int i = 0; i < 10; i++) { r.Frame = 100 + i; r.Present(); }
        Assert.Equal(0, r.Surface.Attaches);
        Assert.Equal(0, r.Surface.Presents);
    }

    [Fact]
    public void Present_exception_breaks_circuit_teardowns_and_fires_onBroken()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        r.Surface.ThrowOnPresent = true;
        r.Present();
        Assert.Equal(1, r.Surface.Teardowns);
        Assert.Equal(1, r.Brokens);
        r.Surface.ThrowOnPresent = false;
        r.Present();
        Assert.Equal(1, r.Surface.Presents);
    }

    [Fact]
    public void Attach_failure_breaks_circuit_without_onBroken()
    {
        var r = new Rig { Surface = { AttachOk = false } };
        for (int i = 0; i < 6; i++) r.Present();
        Assert.Equal(1, r.Surface.Attaches);
        Assert.Equal(0, r.Brokens);
    }

    [Fact]
    public void Null_surface_factory_is_silent_death()
    {
        var r = new Rig { SurfaceAvailable = false };
        for (int i = 0; i < 6; i++) r.Present();
        Assert.Equal(0, r.Surface.Attaches);
        r.Screen.Remove("retire");
        Assert.Equal(0, r.Surface.Teardowns);
    }

    [Fact]
    public void Env_kill_switch_prevents_attach()
    {
        var r = new Rig();
        r.Env["ITSLOADING_NO_MAC_OVERLAY"] = "1";
        for (int i = 0; i < 6; i++) r.Present();
        Assert.Equal(0, r.Surface.Attaches);
    }

    [Fact]
    public void Retire_before_attach_never_touches_surface()
    {
        var r = new Rig();
        r.Present();
        r.Screen.Remove("retire");
        for (int i = 0; i < 5; i++) r.Present();
        Assert.Equal(0, r.Surface.Attaches);
        Assert.Equal(0, r.Surface.Teardowns);
    }

    [Fact]
    public void Remove_then_teardown_is_the_two_phase_retire()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        r.Screen.Remove("retire");
        r.Screen.SetOpacity(0.5);
        Assert.Equal(0.5, r.Surface.LastOpacity);
        r.Screen.Teardown();
        Assert.Equal(1, r.Surface.Teardowns);
        r.Screen.Teardown();
        Assert.Equal(1, r.Surface.Teardowns); // 幂等
    }
}
