using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 启动阶段补丁
// Harmony id: com.somiona.sts2.itsloading.boot
// 步骤/会话/路标三类钩子 + 会话队列反射读取;分数与 span 记录在 BootTimeline。
// 反射字段访问是版本脆弱点(游戏更新改字段名则优雅跳过,见 CacheSessionFields)。

internal static class BootPhasePatches
{
    // ---- 启动子步骤 patch 目标(Essential 同步长黑屏期间的 checkpoints) ----
    // 表序 = 执行序;刻度按实际挂上的步骤均分(BootTimeline.SetEssentialSteps)。
    // Optional = 某些游戏版本没有的目标(如 beta 新增的 AssemblyInfo),缺席静默跳过。
    private static readonly (string Type, string Method, string Label, bool Optional)[] Steps =
    {
        ("MegaCrit.Sts2.Core.Assets.AtlasManager", "LoadEssentialAtlases", "step.atlas", false),
        ("MegaCrit.Sts2.Core.Localization.LocManager", "Initialize", "step.loc", false),
        ("MegaCrit.Sts2.Core.Modding.AssemblyInfo", "Init", "step.asmInfo", true),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Init", "step.modeldb", false),
        ("MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache", "Init", "step.modelIdCache", false),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "InitIds", "step.ids", false),
        ("MegaCrit.Sts2.Core.Multiplayer.Serialization.MessageTypes", "Initialize", "step.msgTypes", false),
        ("MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionTypes", "Initialize", "step.actTypes", false),
    };

    private static readonly Dictionary<MethodBase, string> StepMap = new();

    internal static void Install()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.boot");
        var installed = new List<string>(Steps.Length);
        foreach (var (type, method, label, optional) in Steps)
        {
            var mi = AccessTools.Method(AccessTools.TypeByName(type), method);
            if (mi == null)
            {
                if (!optional) Log.Warn($"[ItsLoading] step not found, skipped: {type}.{method}");
                continue;
            }
            StepMap[mi] = label;
            installed.Add(label);
            harmony.Patch(mi, prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(StepPrefix)));
        }
        // 按实际挂上的步骤重建 Essential 刻度(版本缺步时其余重排)
        ItsLoading.Timeline?.SetEssentialSteps(installed);
        Log.Warn($"[ItsLoading] step patches installed ({StepMap.Count}/{Steps.Length})");

        var essential = AccessTools.Method(
            "MegaCrit.Sts2.Core.Helpers.OneTimeInitialization:ExecuteEssential");
        if (essential != null)
        {
            harmony.Patch(essential,
                postfix: new HarmonyMethod(typeof(BootPhasePatches), nameof(AfterEssential)));
            Log.Warn("[ItsLoading] ExecuteEssential completion patch installed");
        }

        // 存档读取记时(云同步等待之后的收尾段):只进活动日志,不推进度 ——
        // 这段在阶段 3 完成与阶段 4 开始之间,推进度反而要说谎
        var saveManager = AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.SaveManager");
        var progressRead = AccessTools.Method(saveManager, "InitProgressData");
        if (progressRead != null)
        {
            harmony.Patch(progressRead,
                prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(BeforeProgressRead)),
                postfix: new HarmonyMethod(typeof(BootPhasePatches), nameof(AfterProgressRead)));
            Log.Warn("[ItsLoading] progress save read patch installed");
        }
        var prefsRead = AccessTools.Method(saveManager, "InitPrefsData");
        if (prefsRead != null)
        {
            harmony.Patch(prefsRead,
                prefix: new HarmonyMethod(typeof(BootPhasePatches), nameof(BeforePrefsRead)),
                postfix: new HarmonyMethod(typeof(BootPhasePatches), nameof(AfterPrefsRead)));
            Log.Warn("[ItsLoading] prefs save read patch installed");
        }

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

    private static FieldInfo _fName, _fToLoad, _fLoading, _fFinalizing, _fVfx,
        _fVfxLoading, _fCurrentVfx, _fTotal;

    private static void CacheSessionFields(Type t)
    {
        _fName = AccessTools.Field(t, "_name");
        _fToLoad = AccessTools.Field(t, "_toLoad");
        _fLoading = AccessTools.Field(t, "_loading");
        _fFinalizing = AccessTools.Field(t, "_finalizing");
        _fVfx = AccessTools.Field(t, "_vfxScenes");
        _fVfxLoading = AccessTools.Field(t, "_vfxLoading");
        _fCurrentVfx = AccessTools.Field(t, "_currentVfxPath");
        _fTotal = AccessTools.Field(t, "_totalLoaded");
    }

    private static int Count(object queue) => (queue as System.Collections.ICollection)?.Count ?? 0;

    private static string FirstPath(object queue)
    {
        if (queue is not System.Collections.IEnumerable items) return null;
        foreach (object item in items) return item as string;
        return null;
    }

    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static void AfterSessionProcess(object __instance)
    {
        ItsLoading.Run("read session state", () =>
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
            string current = (_fVfxLoading?.GetValue(__instance) is true
                    ? _fCurrentVfx?.GetValue(__instance) as string
                    : null)
                ?? FirstPath(_fFinalizing?.GetValue(__instance))
                ?? FirstPath(_fLoading?.GetValue(__instance))
                ?? FirstPath(_fToLoad?.GetValue(__instance));
            current = ShortPath(current);

            // 计数文案走 Func<int,string>(stat.Total 在时间线;空会话防护也在时间线)
            ItsLoading.Timeline.SessionAdvanced(__instance, name, loaded, remaining, current,
                I18n.T("bar.assets", new() { ["name"] = name }),
                (total, item) => I18n.T("bar.assetsCount", new() { ["n"] = $"{loaded}/{total}" })
                    + (string.IsNullOrEmpty(item) ? "" : $" · {item}"));
        });
    }

    private static void AfterEssential() => ItsLoading.Timeline.EssentialCompleted();

    // ---- 存档读取(活动日志记时,模式同 mod 子步骤:prefix 报开始,postfix 报耗时) ----

    private static long _progressReadTicks, _prefsReadTicks;

    private static void BeforeProgressRead()
    {
        _progressReadTicks = ItsLoading.Sw.ElapsedTicks;
        ItsLoading.Timeline?.Activity(I18n.T("bar.readingProgress"));
    }

    private static void AfterProgressRead()
    {
        long end = ItsLoading.Sw.ElapsedTicks;
        ItsLoading.Timeline?.Activity(I18n.T("bar.readReady", new()
        {
            ["ms"] = $"{(long)((end - _progressReadTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency)}ms",
        }));
    }

    private static void BeforePrefsRead()
    {
        _prefsReadTicks = ItsLoading.Sw.ElapsedTicks;
        ItsLoading.Timeline?.Activity(I18n.T("bar.readingPrefs"));
    }

    private static void AfterPrefsRead()
    {
        long end = ItsLoading.Sw.ElapsedTicks;
        ItsLoading.Timeline?.Activity(I18n.T("bar.prefsReady", new()
        {
            ["ms"] = $"{(long)((end - _prefsReadTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency)}ms",
        }));
    }

    private static void BeforeLogoPlay()
    {
        FreezeProbe.Sample("logo-play");
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

    /// <summary>logo/云同步后的启动收尾。只处理一次(时间线内去重)。</summary>
    private static void BeforeMainMenu()
    {
        ItsLoading.Timeline.Waypoint(BootWaypoint.MainMenu, I18n.T("bar.opening"), $"+{ItsLoading.Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>主菜单已显示(ExecuteDeferred 语义):时间线收尾/冻结/呈现 1.0,本层管注册、摘要与移除。</summary>
    private static void BeforeDeferred()
    {
        FreezeProbe.Sample("menu-ready");
        ItsLoading.Timeline.MenuReady(I18n.T("bar.done"), $"{ItsLoading.Sw.ElapsedMilliseconds}ms");

        // 首次启动的兜底注册:见 WaterfallViewer.RegisterInBaseLib 的注释
        // (此刻 BaseLib 必已加载,LoadedMods 检查在内)
        ItsLoading.Run("register waterfall at menu", WaterfallViewer.RegisterInBaseLib);

        // 一行启动摘要(诊断用,常量级日志;span 数学在时间线)
        string summary = ItsLoading.Timeline.BootSummary();
        if (summary != null) Log.Info(summary);

        // SurfaceRouter 已在 Menu 首帧启动视觉淡出；旧时序是等待 2s 后再
        // 淡出 0.4s，故 +2.4s 才是经过验证的安全销毁点。此前只保持透明
        // layer，不与活跃的渲染 worker 争用资源。
        var tree = (SceneTree)Engine.GetMainLoop();
        var timer = tree.CreateTimer(2.4);
        timer.Timeout += () => ItsLoading.Run("remove bar", ItsLoading.RetireBar);
    }
}
