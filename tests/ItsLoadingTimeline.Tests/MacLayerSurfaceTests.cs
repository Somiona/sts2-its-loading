using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ItsLoading;
using Xunit;

public sealed class MacLayerSurfaceTests
{
    private const BindingFlags All = BindingFlags.NonPublic | BindingFlags.Public
        | BindingFlags.Instance | BindingFlags.Static;
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [Fact]
    public void Gacha_native_layers_follow_the_compiled_plan()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string dir = Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "gachathespire");
        var surface = new MacLayerSurface(ThemeCompiler.Compile(dir), dir, "test");
        Invoke(surface, "Build", 854d, 480d);

        object row1 = ((IEnumerable)Field(surface, "_rows")).Cast<object>()
            .Single(row => (string)Field(row, "Id") == "row1");
        var slots = (IntPtr[])Field(row1, "Slots");
        double[] xs = slots.Select(slot => Number(Field(
            Invoke(surface, "ObjcRect", slot, Selector(surface, "frame")), "X"))).ToArray();
        Assert.Equal(7, xs.Distinct().Count());
        Assert.All(slots, slot => Assert.NotEqual(IntPtr.Zero,
            (IntPtr)Invoke(surface, "ObjcId", slot, Selector(surface, "contents"))));

        object log = ((IEnumerable)Field(surface, "_logs")).Cast<object>().Single();
        IntPtr firstLine = ((IntPtr[])Field(log, "Lines"))[0];
        Assert.Equal("center", ReadCfString((IntPtr)Invoke(
            surface, "ObjcId", firstLine, Selector(surface, "alignmentMode"))));

        object mask = Field(surface, "_mask");
        IntPtr maskLayer = (IntPtr)Field(mask, "MaskLayer");
        IntPtr container = (IntPtr)Invoke(surface, "ObjcId", maskLayer, Selector(surface, "superlayer"));
        double maskY = Number(Field(Invoke(surface, "ObjcRect", container,
            Selector(surface, "frame")), "Y"));
        Assert.Equal(95.3, maskY, 1);
        Invoke(surface, "UpdateMask", Frame(indeterminate: true));
        Assert.True(Animation(surface, maskLayer, "itsloading.indeterminate.position")
            != IntPtr.Zero, "mask indeterminate loop was not installed");
        Invoke(surface, "UpdateMask", Frame(indeterminate: false));
        Assert.Equal(IntPtr.Zero,
            Animation(surface, maskLayer, "itsloading.indeterminate.position"));
        surface.Teardown();
    }

    [Fact]
    public void Native_surface_applies_fractional_opacity_to_its_root_layer()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var surface = new MacLayerSurface(null, "", "test");
        Invoke(surface, "Build", 854d, 480d);
        SetField(surface, "_built", true);

        surface.SetOpacity(0.5);

        IntPtr root = (IntPtr)Field(surface, "_root");
        Assert.Equal(0.5f, ObjcFloat(root, sel_registerName("opacity")), 3);
        surface.Teardown();
    }

    [Fact]
    public void Native_motion_runs_in_core_animation_and_data_only_adds_activity()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string dir = Path.Combine(RepoRoot(), "src", "ItsLoading", "themes", "minespire");
        var surface = new MacLayerSurface(ThemeCompiler.Compile(dir), dir, "test");
        Invoke(surface, "Build", 854d, 480d);

        object sprite = ((IEnumerable)Field(surface, "_sprites")).Cast<object>().Single();
        IntPtr spriteLayer = (IntPtr)Field(sprite, "Layer");
        IntPtr keys = (IntPtr)Invoke(surface, "ObjcId", spriteLayer,
            Selector(surface, "animationKeys"));
        ulong keyCount = (ulong)Invoke(surface, "ObjcCount", keys, Selector(surface, "count"));
        Assert.True(keyCount > 0, "sprite layer has no animations");
        Assert.True(Animation(surface, spriteLayer, "itsloading.sprite.loop") != IntPtr.Zero,
            "sprite loop was not installed");
        Assert.Equal(3.5, LayerDouble(surface, Animation(surface, spriteLayer,
            "itsloading.sprite.loop"), "duration"), 3);

        Invoke(surface, "AdvanceActiveSprites");
        Invoke(surface, "AdvanceActiveSprites");
        Assert.Equal(0.25, LayerDouble(surface, spriteLayer, "timeOffset"), 3);

        object local = ((IEnumerable)Field(surface, "_bars")).Cast<object>()
            .Single(bar => (ThemeBind)Field(bar, "Bind") == ThemeBind.Local);
        Invoke(surface, "UpdateBars", Frame(indeterminate: true));
        Assert.True(Animation(surface, (IntPtr)Field(local, "Fill"),
            "itsloading.indeterminate.position") != IntPtr.Zero,
            "indeterminate loop was not installed");
        Invoke(surface, "UpdateBars", Frame(indeterminate: false));
        Assert.Equal(IntPtr.Zero, Animation(surface, (IntPtr)Field(local, "Fill"),
            "itsloading.indeterminate.position"));
        surface.Teardown();
    }

    private static object Invoke(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethods(All)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        return method.Invoke(target, args)!;
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, All)!.GetValue(target)!;

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, All)!.SetValue(target, value);

    private static double Number(object value) => Convert.ToDouble(value);
    private static IntPtr Selector(object target, string name) => (IntPtr)Invoke(target, "Sel", name);

    private static double LayerDouble(object bridge, IntPtr target, string name) =>
        Number(Invoke(bridge, "ObjcDouble", target, Selector(bridge, name)));

    private static IntPtr Animation(object surface, IntPtr layer, string key)
    {
        IntPtr value = (IntPtr)Invoke(surface, "CfString", key);
        try
        {
            return (IntPtr)Invoke(surface, "ObjcIdPtrRet", layer,
                Selector(surface, "animationForKey:"), value);
        }
        finally
        {
            Invoke(surface, "CfRelease", value);
        }
    }

    private static LoadingFrame Frame(bool indeterminate) => new(
        BootStage.Mods, 0.2f, indeterminate ? -1f : 0.2f, indeterminate,
        1, false, "step", "detail", Array.Empty<string>(), false);

    private static string ReadCfString(IntPtr value)
    {
        var buffer = new byte[64];
        Assert.True(CFStringGetCString(value, buffer, buffer.Length, 0x08000100));
        return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ItsLoading", "themes")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    [DllImport(CoreFoundation)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern float ObjcFloat(IntPtr self, IntPtr selector);
}
