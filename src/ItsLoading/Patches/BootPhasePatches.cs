using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 启动阶段补丁
// Harmony id: com.somiona.sts2.itsloading.boot(架构拆分 #4:注册 + 钩子同居一族文件)
// 步骤/会话/路标三类钩子 + 会话队列反射读取;分数与 span 记录在 BootTimeline。
// 反射字段访问是版本脆弱点(游戏更新改字段名则优雅跳过,见 CacheSessionFields)。

internal static class BootPhasePatches
{
    // ---- 启动子步骤 patch 目标(Essential 同步长黑屏期间的 checkpoints) ----
    // 刻度分数的唯一真源在 BootTimeline.StepFractions;此处只留定位信息(Type/Method)与 join 键(Label)。
    private static readonly (string Type, string Method, string Label)[] Steps =
    {
        ("MegaCrit.Sts2.Core.Assets.AtlasManager", "LoadEssentialAtlases", "step.atlas"),
        ("MegaCrit.Sts2.Core.Localization.LocManager", "Initialize", "step.loc"),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Init", "step.modeldb"),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "InitIds", "step.ids"),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Preload", "step.preload"),
    };

    private static readonly Dictionary<MethodBase, string> StepMap = new();

    internal static void Install()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.boot");
        foreach (var (type, method, label) in Steps)
        {
            var mi = AccessTools.Method(AccessTools.TypeByName(type), method);
            if (mi == null)
            {
                Log.Warn($"[ItsLoading] step not found, skipped: {type}.{method}");
                continue;
            }
            StepMap[mi] = label;
            harmony.Patch(mi, prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(StepPrefix)));
        }
        Log.Warn($"[ItsLoading] step patches installed ({StepMap.Count}/{Steps.Length})");

        var menu = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:LaunchMainMenu");
        if (menu != null)
        {
            harmony.Patch(menu, prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(BeforeMainMenu)));
            Log.Warn("[ItsLoading] LaunchMainMenu patch installed");
        }

        // 资产会话:每帧真实进度(主线程 _Process 泵,帧自然流动,无需 ForceDraw)
        var sessionProcess = AccessTools.Method(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.AssetLoadingSession"), "Process");
        if (sessionProcess != null)
        {
            harmony.Patch(sessionProcess,
                postfix: new HarmonyMethod(typeof(BootPhasePatches), nameof(AfterSessionProcess)));
            Log.Warn("[ItsLoading] AssetLoadingSession.Process patch installed");
        }

        var logoPlay = AccessTools.Method(
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NLogoAnimation:PlayAnimation");
        if (logoPlay != null)
        {
            harmony.Patch(logoPlay, prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(BeforeLogoPlay)));
            Log.Warn("[ItsLoading] PlayAnimation patch installed");
        }

        var loadMenu = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:LoadMainMenu");
        if (loadMenu != null)
        {
            harmony.Patch(loadMenu, prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(BeforeLoadMenu)));
            Log.Warn("[ItsLoading] LoadMainMenu patch installed");
        }

        var deferred = AccessTools.Method("MegaCrit.Sts2.Core.Helpers.OneTimeInitialization:ExecuteDeferred");
        if (deferred != null)
        {
            harmony.Patch(deferred, prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(BeforeDeferred)));
            Log.Warn("[ItsLoading] ExecuteDeferred patch installed");
        }
    }

    // ---- 资产会话 postfix:反射读队列实时状态 ----

    private static FieldInfo _fName, _fToLoad, _fLoading, _fFinalizing, _fVfx, _fVfxLoading, _fTotal;

    private static void CacheSessionFields(Type t)
    {
        _fName = AccessTools.Field(t, "_name");
        _fToLoad = AccessTools.Field(t, "_toLoad");
        _fLoading = AccessTools.Field(t, "_loading");
        _fFinalizing = AccessTools.Field(t, "_finalizing");
        _fVfx = AccessTools.Field(t, "_vfxScenes");
        _fVfxLoading = AccessTools.Field(t, "_vfxLoading");
        _fTotal = AccessTools.Field(t, "_totalLoaded");
    }

    private static int Count(object queue) => (queue as System.Collections.ICollection)?.Count ?? 0;

    private static void AfterSessionProcess(object __instance)
    {
        try
        {
            if (ItsLoading.Timeline == null) return;
            if (_fName == null) CacheSessionFields(__instance.GetType());

            string name = _fName?.GetValue(__instance) as string ?? "";
            // 未知会话/已冻结时先跳过(游戏内房间会话每帧都来,反射读必须廉价)
            if (!ItsLoading.Timeline.TracksSession(name)) return;

            int remaining = Count(_fToLoad?.GetValue(__instance))
                          + Count(_fLoading?.GetValue(__instance))
                          + Count(_fFinalizing?.GetValue(__instance))
                          + Count(_fVfx?.GetValue(__instance))
                          + ((_fVfxLoading?.GetValue(__instance) is true) ? 1 : 0);
            int loaded = _fTotal?.GetValue(__instance) as int? ?? 0;

            // 计数文案走 Func<int,string>(stat.Total 在时间线;空会话防护也在时间线)
            ItsLoading.Timeline.SessionAdvanced(__instance, name, loaded, remaining,
                I18n.T("bar.assets", new() { ["name"] = name }),
                total => I18n.T("bar.assetsCount", new() { ["n"] = $"{loaded}/{total}" }));
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to read session state: {e}");
        }
    }

    private static void BeforeLogoPlay()
    {
        ItsLoading.Timeline.Waypoint(BootWaypoint.Logo, I18n.T("bar.logo"), "");
    }

    private static void BeforeLoadMenu()
    {
        ItsLoading.Timeline.Waypoint(BootWaypoint.MenuLoad, I18n.T("bar.menuIn"), "");
    }

    private static void StepPrefix(MethodBase __originalMethod)
    {
        if (!StepMap.TryGetValue(__originalMethod, out var label)) return;
        // 步骤级耗时:相邻差分收尾在时间线;文案就地解析
        ItsLoading.Timeline.StepStarted(label, I18n.T(label), $"+{ItsLoading.Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>logo/云同步后的启动收尾(LaunchMainMenu 调用点已逆向确认)。只处理一次(时间线内去重)。</summary>
    private static void BeforeMainMenu()
    {
        ItsLoading.Timeline.Waypoint(BootWaypoint.MainMenu, I18n.T("bar.opening"), $"+{ItsLoading.Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>主菜单已显示(ExecuteDeferred 语义):时间线收尾/冻结/呈现 1.0,本层管注册、摘要与移除。</summary>
    private static void BeforeDeferred()
    {
        ItsLoading.Timeline.MenuReady(I18n.T("bar.done"), $"{ItsLoading.Sw.ElapsedMilliseconds}ms");

        // 首启兜底注册瀑布图入口:见 WaterfallViewer.RegisterInBaseLib 的注释
        // (此刻 BaseLib 必已加载,LoadedMods 检查在内)
        ItsLoading.Run("register waterfall at menu", WaterfallViewer.RegisterInBaseLib);

        // 一行启动摘要(诊断用,常量级日志;span 数学在时间线)
        string summary = ItsLoading.Timeline.BootSummary();
        if (summary != null) Log.Info(summary);

        var tree = (SceneTree)Engine.GetMainLoop();
        var timer = tree.CreateTimer(2.0);
        timer.Timeout += () => ItsLoading.Run("remove bar", ItsLoading.RetireBar);
    }
}
