using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

// ---------------------------------------------------------------- mod 加载链补丁
// Harmony id: com.somiona.sts2.itsloading
// 钩子只报事实:诊断文本就地构造,计数/分数/span 全部交给 BootTimeline。

internal static class LoaderPatches
{
    private static long _lastMs; // 逐 mod 毫秒差(诊断文本用)
    private static int _lastG0, _lastG1, _lastG2; // 上个 postfix 时的 GC 回收次数(缝隙归因用)
    private static long _initStartTicks; // 当前初始化器起始(活动日志文案 + Api span 共用)
    private static long _packStartTicks; // 当前资源包挂载起始

    internal static void Install()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading");
        var original = AccessTools.Method(typeof(ModManager), "TryLoadMod");
        if (original == null)
        {
            Log.Error("[ItsLoading] TryLoadMod not found — game update changed signature?");
            return;
        }
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(LoaderPatches), nameof(BeforeTryLoadMod)),
            postfix: new HarmonyMethod(typeof(LoaderPatches), nameof(AfterModLoad)));
        Log.Warn("[ItsLoading] TryLoadMod prefix+postfix installed OK");

        // mod 加载内部子步骤(活动日志用,不推进进度条):TryLoadMod 内部依次是
        // 程序集加载 → pck 挂载 → 反射扫描 → 初始化器执行(大头)。程序集加载是
        // BCL 方法不挂钩;其余两个挂游戏/GodotSharp 自有方法。
        var initializer = AccessTools.Method(
            "MegaCrit.Sts2.Core.Modding.ModManager:CallModInitializer");
        if (initializer != null)
        {
            harmony.Patch(initializer,
                prefix: new HarmonyMethod(typeof(LoaderPatches), nameof(BeforeInitializer)),
                postfix: new HarmonyMethod(typeof(LoaderPatches), nameof(AfterInitializer)));
            Log.Warn("[ItsLoading] CallModInitializer sub-step patch installed");
        }
        var loadPack = AccessTools.Method(typeof(Godot.ProjectSettings),
            nameof(Godot.ProjectSettings.LoadResourcePack));
        if (loadPack != null)
        {
            harmony.Patch(loadPack,
                prefix: new HarmonyMethod(typeof(LoaderPatches), nameof(BeforePackMounted)),
                postfix: new HarmonyMethod(typeof(LoaderPatches), nameof(AfterPackMounted)));
            Log.Warn("[ItsLoading] LoadResourcePack sub-step patch installed");
        }
    }

    private static void BeforeTryLoadMod(Mod mod)
    {
        // 缝隙归因:两次 TryLoadMod 之间是游戏的裸 foreach(无游戏代码),
        // >100ms 即主线程被暂停——看 GC 各代回收次数是否跨缝增加。
        long now = ItsLoading.Sw.ElapsedMilliseconds;
        if (_lastMs > 0 && now - _lastMs > 100)
        {
            Log.Warn($"[ItsLoading] inter-mod gap {now - _lastMs}ms before {mod.manifest?.id} " +
                     $"(GC delta gen0={GC.CollectionCount(0) - _lastG0} " +
                     $"gen1={GC.CollectionCount(1) - _lastG1} " +
                     $"gen2={GC.CollectionCount(2) - _lastG2})");
        }
        string id = mod.manifest?.id ?? "<null>";
        // 在真正进入昂贵初始化前提交“正在加载哪个 mod”;该状态会自然停留整个加载时长。
        ItsLoading.Timeline.ModStarted(
            I18n.T("bar.mods", new()
            {
                ["n"] = ItsLoading.Timeline.Count.ToString(),
                ["t"] = ItsLoading.Timeline.Total.ToString(),
            }),
            I18n.T("bar.loadingMod", new() { ["id"] = id }));
    }

    private static void AfterModLoad(Mod mod)
    {
        long now = ItsLoading.Sw.ElapsedMilliseconds;
        long delta = now - _lastMs;
        _lastMs = now;
        _lastG0 = GC.CollectionCount(0);
        _lastG1 = GC.CollectionCount(1);
        _lastG2 = GC.CollectionCount(2);
        string id = mod.manifest?.id ?? "<null>";

        // 计数在时间线:计数依赖的文案以 Func<int,string> 传入,末个 mod 完成时呈现 0.60
        int n = ItsLoading.Timeline.ModLoaded(
            id, mod.state.ToString(),
            count => I18n.T("bar.mods", new() { ["n"] = count.ToString(), ["t"] = ItsLoading.Timeline.Total.ToString() }),
            I18n.T("bar.modLoaded", new() { ["id"] = id, ["ms"] = $"{delta}ms" }),
            I18n.T("bar.modsDone"),
            count => I18n.T("bar.modsDoneDetail",
                new() { ["n"] = count.ToString(), ["ms"] = $"{ItsLoading.Sw.ElapsedMilliseconds}ms" }));

        Log.Warn($"[ItsLoading] [{n}/{ItsLoading.Timeline.Total}] {id} -> {mod.state} " +
                 $"+{delta}ms frame={Engine.GetFramesDrawn()}");

        // BaseLib 加载完成的瞬间注册瀑布图入口(软依赖:编译期引用 + JIT 方法级隔离,
        // BaseLib 缺席时 RegisterInBaseLib 永不被调用、类型永不加载)
        if (id == "BaseLib" && mod.state == ModLoadState.Loaded)
        {
            ItsLoading.Run("register in BaseLib", WaterfallViewer.RegisterInBaseLib);
        }

        if (ItsLoading.Timeline.ModsDone)
        {
            Log.Warn($"[ItsLoading] all mods processed @ +{ItsLoading.Sw.ElapsedMilliseconds}ms");
        }
    }

    // ---- mod 加载内部子步骤(仅活动日志;冻结期间在日志里积累,帧恢复后可见) ----

    private static void BeforeInitializer(Type initializerType)
    {
        _initStartTicks = ItsLoading.Sw.ElapsedTicks;
        ItsLoading.Timeline?.Activity(
            I18n.T("bar.initializing", new() { ["type"] = initializerType.Name }));
    }

    private static void AfterInitializer(Type initializerType)
    {
        long end = ItsLoading.Sw.ElapsedTicks;
        long ms = (long)((end - _initStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        ItsLoading.Timeline?.ModSubStep("init " + initializerType.Name, _initStartTicks, end);
        ItsLoading.Timeline?.Activity(I18n.T("bar.initialized", new()
        {
            ["type"] = initializerType.Name,
            ["ms"] = $"{ms}ms",
        }));
    }

    private static void BeforePackMounted(string pack)
    {
        _packStartTicks = ItsLoading.Sw.ElapsedTicks;
    }

    private static void AfterPackMounted(string pack)
    {
        long end = ItsLoading.Sw.ElapsedTicks;
        int slash = Math.Max(pack.LastIndexOf('/'), pack.LastIndexOf('\\'));
        string name = slash >= 0 ? pack[(slash + 1)..] : pack;
        long ms = (long)((end - _packStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        ItsLoading.Timeline?.ModSubStep("pck " + name, _packStartTicks, end);
        ItsLoading.Timeline?.Activity(I18n.T("bar.pckMounted", new()
        {
            ["name"] = name,
            ["ms"] = $"{ms}ms",
        }));
    }
}
