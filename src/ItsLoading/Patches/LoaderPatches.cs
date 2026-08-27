using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

// ---------------------------------------------------------------- mod 加载链补丁
// Harmony id: com.somiona.sts2.itsloading(架构拆分 #4:注册 + 钩子同居一族文件)
// 钩子只报事实:诊断文本就地构造,计数/分数/span 全部交给 BootTimeline。

internal static class LoaderPatches
{
    private static long _lastMs; // 逐 mod 毫秒差(诊断文本用)

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
    }

    private static void BeforeTryLoadMod(Mod mod)
    {
        // 单次时钟读;mod 段总区间起点 + 突发期前缀补画(经 Presenter 出帧)
        ItsLoading.Timeline.ModStarted();
    }

    private static void AfterModLoad(Mod mod)
    {
        long now = ItsLoading.Sw.ElapsedMilliseconds;
        long delta = now - _lastMs;
        _lastMs = now;
        string id = mod.manifest?.id ?? "<null>";

        // 计数在时间线:计数依赖的文案以 Func<int,string> 传入,末个 mod 完成时呈现 0.60
        int n = ItsLoading.Timeline.ModLoaded(
            id, mod.state.ToString(),
            count => I18n.T("bar.mods", new() { ["n"] = count.ToString(), ["t"] = ItsLoading.Timeline.Total.ToString() }),
            $"{id} · +{delta}ms",
            I18n.T("bar.modsDone"),
            count => $"{count} · {ItsLoading.Sw.ElapsedMilliseconds}ms");

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
}
