using System;
using System.Collections.Generic;
using System.Linq;
using ItsLoading;
using Xunit;

#nullable enable

public sealed class SurfaceRouterTests
{
    private sealed class FakeGodot : IGodotSurface
    {
        public int Presents, Retires;
        public bool Visible = true;
        public LoadingFrame Last;
        public void Present(LoadingFrame frame) { Presents++; Last = frame; }
        public void SetVisible(bool visible) => Visible = visible;
        public void Retire() => Retires++;
    }

    private sealed class FakeNative : IThemeSurface
    {
        public int Attaches, Presents, Teardowns;
        public bool AttachOk = true;
        public bool ThrowOnPresent;
        public readonly List<double> Opacities = new();
        public LoadingFrame Last;

        public bool TryAttach() { Attaches++; return AttachOk; }
        public void Present(LoadingFrame frame)
        {
            if (ThrowOnPresent) throw new InvalidOperationException("boom");
            Presents++;
            Last = frame;
        }
        public void Teardown() => Teardowns++;
        public void SetOpacity(double opacity) => Opacities.Add(opacity);
    }

    private sealed class Rig
    {
        public long Drawn = 5;
        public readonly FakeGodot Godot = new();
        public readonly FakeNative Native = new();
        public readonly Dictionary<string, string?> Env = new();
        public bool NativeAvailable = true;
        public int FactoryCalls;
        public readonly List<int> Delays = new();
        public readonly SurfaceRouter Router;

        public Rig(bool allowNative = true)
        {
            Router = new SurfaceRouter(
                Godot,
                () => Drawn,
                () => true,
                k => Env.TryGetValue(k, out var value) ? value : null,
                allowNative ? () => { FactoryCalls++; return NativeAvailable ? Native : null; } : null,
                _ => { }, ms =>
                {
                    Delays.Add(ms);
                    return System.Threading.Tasks.Task.CompletedTask;
                });
        }

        public void Present(int stage = 2, string step = "s") => Router.Present(Frame(stage, step));
    }

    private static LoadingFrame Frame(int stage = 2, string step = "s") =>
        new((BootStage)stage, 0.5f, 0.5f, false, 1.0, false,
            step, "detail", new[] { step, "detail" }, false);

    [Fact]
    public void Godot_is_the_only_path_during_warmup()
    {
        var r = new Rig();
        for (int i = 0; i < 3; i++) r.Present();
        Assert.Equal(3, r.Godot.Presents);
        Assert.Equal(0, r.Native.Attaches);
    }

    [Fact]
    public void Native_switches_only_after_attach_and_first_present_succeed()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        Assert.True(r.Router.NativeActive);
        Assert.Equal(4, r.Godot.Presents);
        Assert.Equal(1, r.Native.Attaches);
        Assert.Equal(1, r.Native.Presents);
        Assert.False(r.Godot.Visible);

        r.Present(stage: 3, step: "next");
        Assert.Equal(4, r.Godot.Presents);
        Assert.Equal(2, r.Native.Presents);
        Assert.Equal("next", r.Native.Last.StepText);
    }

    [Fact]
    public void Native_disabled_does_not_construct_native_or_change_godot()
    {
        var r = new Rig(allowNative: false);
        for (int i = 0; i < 8; i++) r.Present();
        Assert.Equal(8, r.Godot.Presents);
        Assert.Equal(0, r.FactoryCalls);
    }

    [Fact]
    public void Moving_frames_stay_on_godot_but_a_later_freeze_can_attach()
    {
        var r = new Rig();
        for (int i = 0; i < 6; i++) { r.Drawn++; r.Present(); }
        Assert.Equal(0, r.Native.Attaches);
        for (int i = 0; i < 4; i++) r.Present();
        Assert.True(r.Router.NativeActive);
    }

    [Fact]
    public void Attach_failure_keeps_the_existing_godot_surface()
    {
        var r = new Rig { Native = { AttachOk = false } };
        for (int i = 0; i < 8; i++) r.Present();
        Assert.False(r.Router.NativeActive);
        Assert.Equal(8, r.Godot.Presents);
        Assert.Equal(1, r.Native.Attaches);
    }

    [Fact]
    public void Native_failure_resumes_godot_with_the_latest_frame()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        r.Native.ThrowOnPresent = true;
        r.Present(stage: 4, step: "latest");
        Assert.False(r.Router.NativeActive);
        Assert.Equal(5, r.Godot.Presents);
        Assert.Equal("latest", r.Godot.Last.StepText);
        Assert.True(r.Godot.Visible);
        Assert.Equal(1, r.Native.Teardowns);
    }

    [Fact]
    public void Environment_kill_switch_keeps_godot_primary()
    {
        var env = new Dictionary<string, string?> { ["ITSLOADING_NO_MAC_OVERLAY"] = "1" };
        var godot = new FakeGodot();
        var native = new FakeNative();
        var router = new SurfaceRouter(godot, () => 1, () => true,
            k => env.TryGetValue(k, out var value) ? value : null, () => native);
        for (int i = 0; i < 8; i++) router.Present(Frame());
        Assert.Equal(8, godot.Presents);
        Assert.Equal(0, native.Attaches);
    }

    [Fact]
    public void Retire_fades_and_releases_both_paths_once()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();
        r.Router.Retire("done").GetAwaiter().GetResult();
        r.Router.Retire("done-again").GetAwaiter().GetResult();
        Assert.Equal(1, r.Godot.Retires);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => 1.0 - i / 16.0),
            r.Native.Opacities);
        Assert.Equal(333, r.Delays.Sum());
        Assert.Equal(1, r.Native.Teardowns);
    }

    [Fact]
    public void Final_stage_fades_but_waits_for_safe_retire_to_teardown()
    {
        var r = new Rig();
        for (int i = 0; i < 4; i++) r.Present();

        r.Present(stage: (int)BootStage.Menu, step: "done");

        Assert.Equal(BootStage.Menu, r.Native.Last.Stage);
        Assert.Equal(2, r.Native.Presents);
        Assert.Equal(1, r.Godot.Retires);
        Assert.Equal(0.0, r.Native.Opacities[^1]);
        Assert.Equal(0, r.Native.Teardowns);
        r.Present(stage: 6, step: "late");
        Assert.Equal(2, r.Native.Presents);

        r.Router.Retire("safe-point").GetAwaiter().GetResult();
        Assert.Equal(1, r.Native.Teardowns);
    }

    [Fact]
    public void Final_stage_retires_the_godot_only_path_after_its_last_frame()
    {
        var r = new Rig(allowNative: false);

        r.Present(stage: (int)BootStage.Menu, step: "done");

        Assert.Equal(1, r.Godot.Presents);
        Assert.Equal(BootStage.Menu, r.Godot.Last.Stage);
        Assert.Equal(1, r.Godot.Retires);
    }
}
