using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
#nullable enable

namespace ItsLoading;

// ---------------------------------------------------------------- 启动时间线
//
// 进度与 span 只由本类写入:Harmony 钩子只上报事实(mod/步骤/会话/路标)——
//   · 0→1 进度刻度表(Steps 分数、SessionRanges 子区间、路标分数)
//   · 全部 span 记录(Api.LoadingDurations 只读查询)
//   · 锚点与时钟换算(EngineOffset、BootAnchor、TotalBootMs)
//   · 冻结语义(菜单就绪后数据封存)
// 呈现为 push 模型:每个事件发布一份完整 LoadingViewState(全程 + 当前阶段
// 双进度)给 Presenter,Present 调用密度 = 真实加载活动密度。
// 文案由钩子解析后传入(I18n 不进本类);纯 BCL、双时钟注入,可离线单测(tests/)。

/// <summary>启动路标。</summary>
internal enum BootWaypoint { Logo, MenuLoad, MainMenu }

internal sealed class BootTimeline
{
    // ---- 全程刻度表(主题另有当前阶段 0→1 局部条) ----

    internal const float WorkshopEnd = 0.25f;
    internal const float ModsEnd = 0.60f;
    internal const float EssentialEnd = 0.66f;
    internal const float OpeningAssetsEnd = 0.70f;
    internal const float MainMenuAssetsEnd = 0.82f;
    internal const float IntroEnd = 0.88f;

    /// <summary>
    /// Essential 子步骤的执行序(两版通用,beta 在 loc 后多 AssemblyInfo)。
    /// 刻度按「实际挂上的步骤」均分重建(见 SetEssentialSteps):某版本缺某步时
    /// 其余步骤自动重排,不维护两套硬编码表。默认集 = 经典四步(patch 安装
    /// 失败的兜底,也是测试基线)。
    /// </summary>
    private static readonly string[] EssentialStepOrder =
    {
        "step.atlas", "step.loc", "step.asmInfo", "step.modeldb",
        "step.modelIdCache", "step.ids", "step.msgTypes", "step.actTypes",
    };

    private static readonly string[] DefaultEssentialSteps =
        { "step.atlas", "step.loc", "step.modeldb", "step.ids" };

    /// <summary>
    /// 启动期的会话名 → 阶段 + 全程条子区间。游戏内会话(房间/角色)不在此表,自动忽略。
    /// 注意没有 "MainMenu" 会话:游戏唯一创建它的 PreloadManager.LoadMainMenuAssets 零调用者,
    /// 菜单资产实际由 "Common" 会话加载(LoadCommonAndMainMenuAssets),但那发生在
    /// ExecuteDeferred(=条的 1.0 完成点与 Freeze 点)之后、条移除前的 2 秒停留期内——
    /// 若映射它,会把已显示 1.0「完成」的条拽回 0.88。启动边界定在菜单就绪,延迟资产
    /// 属启动后的后台工作,不进条也不进冻结的 Api 数据。
    /// </summary>
    private static readonly Dictionary<string, (BootStage Stage, float Start, float End)> SessionRanges = new()
    {
        { "IntroLogo", (BootStage.OpeningAssets, EssentialEnd, OpeningAssetsEnd) },
        { "MainMenuEssentials", (BootStage.MainMenuAssets, OpeningAssetsEnd, MainMenuAssetsEnd) },
    };

    private static readonly Dictionary<BootWaypoint, float> WaypointFractions = new()
    {
        { BootWaypoint.Logo, MainMenuAssetsEnd },
        { BootWaypoint.MenuLoad, IntroEnd },
        { BootWaypoint.MainMenu, EssentialEnd },
    };

    // ---- 状态(主线程独占写入) ----

    /// <summary>呈现回调(push 模型):主题只消费完整快照。</summary>
    public Action<LoadingViewState>? Presenter;

    private readonly Func<long> _engineMsec;
    private readonly Func<long> _swTicks;
    private readonly double _engineOffsetMs; // C# Stopwatch 时间轴 → 引擎时间轴(gd 第 0 帧起算)

    internal bool Frozen { get; private set; }
    internal bool ModsDone { get; private set; }
    internal int Count { get; private set; } = 1;
    internal int Total { get; private set; } = 1;
    internal int PrefixCalls;                    // 诊断:prefix 实际触发次数
    internal double TotalBootMs => _totalBootMs;

    private double _totalBootMs = -1;
    private long _bootAnchorMsec = -1;
    private long _modStartTicks, _firstModTicks = -1, _lastModTicks, _lastStepTicks = -1;
    private string? _currentModId; // 当前正在加载的 mod(子步骤 span 的归属)
    private long _boundaryTicks = -1;      // 路标段开点(-1 = 无开段)
    private string? _boundaryLabel;        // 开段 i18n key
    private bool _menuHandled;
    private LoadingViewState _current = new(BootStage.Mods, WorkshopEnd, 0f, null, null, false);
    internal LoadingViewState Current => _current;

    internal readonly List<Api.LoadSpan> ModSpans = new(capacity: 64);
    internal readonly List<Api.LoadSpan> SubStepSpans = new(capacity: 128);
    internal readonly List<Api.LoadSpan> WorkshopSpans = new(capacity: 64);
    internal readonly List<Api.LoadSpan> StepSpans = new(capacity: 16);
    internal readonly List<Api.LoadSpan> SessionSpans = new(capacity: 8);
    internal readonly List<Api.LoadSpan> PhaseSpans = new(capacity: 8);
    internal readonly List<Api.LoadSpan> WaypointSpans = new(capacity: 8);

    // ConditionalWeakTable:session 结束后可被 GC,不阻止回收;值对象复用,无 per-frame 分配
    private readonly ConditionalWeakTable<object, SessionStat> _sessionStats = new();

    private sealed class SessionStat
    {
        public int Total;
        public long FirstTicks, LastTicks;
        public bool Recorded;
        public string? CurrentItem;
    }

    public BootTimeline(Func<long> engineMsec, Func<long> swTicks)
    {
        _engineMsec = engineMsec;
        _swTicks = swTicks;
        // 记录双时钟偏移(必须先于任何钩子的 ToEngineMs)
        _engineOffsetMs = engineMsec() - swTicks() * SwTicksToMs;
        StepFractions = BuildStepFractions(DefaultEssentialSteps);
    }

    /// <summary>label → (Overall, Local),步骤 prefix 时刻的刻度:第 i 步从 i/N 起,EssentialCompleted 收 1.0。</summary>
    private Dictionary<string, (float Overall, float Local)> StepFractions { get; set; }

    private static Dictionary<string, (float, float)> BuildStepFractions(string[] labels)
    {
        var dict = new Dictionary<string, (float, float)>(labels.Length);
        for (int i = 0; i < labels.Length; i++)
        {
            dict[labels[i]] = (
                ModsEnd + (EssentialEnd - ModsEnd) * i / labels.Length,
                (float)i / labels.Length);
        }
        return dict;
    }

    /// <summary>patch 安装后按实际挂上的步骤(执行序)重建刻度表。</summary>
    internal void SetEssentialSteps(IEnumerable<string> installedLabels) =>
        StepFractions = BuildStepFractions(
            installedLabels.OrderBy(l => Array.IndexOf(EssentialStepOrder, l)).ToArray());

    private static double SwTicksToMs => 1000.0 / Stopwatch.Frequency;
    private double ToEngineMs(long swTicks) => swTicks * SwTicksToMs + _engineOffsetMs;

    private void Present(BootStage stage, float overall, float local,
        string? step, string? detail, bool forceDraw)
    {
        // 阶段和全程条都永不倒退；阶段内条允许在合法阶段切换时重置。
        if (stage < _current.Stage) return;
        overall = Math.Max(_current.Overall, Math.Clamp(overall, 0f, 1f));
        if (local >= 0f) local = Math.Clamp(local, 0f, 1f);
        _current = new LoadingViewState(
            stage, overall, local,
            step ?? _current.Step,
            detail ?? _current.Detail,
            forceDraw);
        Presenter?.Invoke(_current);
    }

    internal void Replay(bool forceDraw = true) =>
        Presenter?.Invoke(_current with { ForceDraw = forceDraw });

    /// <summary>
    /// 纯日志事件:只更新细节文案,不推进任何进度(overall/local 原样)、不记 span、
    /// 不强制出帧。给「mod 加载内部子步骤」(初始化器/资源包挂载)等更细粒度用。
    /// </summary>
    internal void Activity(string text)
    {
        if (Frozen) return;
        _current = _current with { Detail = text, ForceDraw = false };
        Presenter?.Invoke(_current);
    }

    // ---- 写入口:钩子上报事实 ----

    /// <summary>mod 段开始。processed 含本 mod 与本次启动中已经先于它处理的 mod。</summary>
    internal void BeginMods(int total, int processed, string stepText)
    {
        Total = Math.Max(1, total);
        Count = Math.Clamp(processed, 1, Total);
        float local = Count / (float)Total;
        if (Count >= Total) ModsDone = true;
        Present(BootStage.Mods, WorkshopEnd + (ModsEnd - WorkshopEnd) * local,
            local, stepText, "", false);
    }

    /// <summary>TryLoadMod prefix:在昂贵初始化前显示当前 mod，并强制提交一次。
    /// detail 为本地化文案(活动日志呈现),id 为裸 mod id(子步骤 span 的归属,
    /// 与 ModLoaded 的 span Id 同源)。</summary>
    internal void ModStarted(string stepText, string detail, string id)
    {
        if (Frozen) return;
        PrefixCalls++;
        _currentModId = id;
        _modStartTicks = _swTicks();
        if (_firstModTicks < 0) _firstModTicks = _modStartTicks;
        Present(BootStage.Mods, _current.Overall, Count / (float)Total,
            stepText, detail, true);
    }

    /// <summary>
    /// 工坊扫描时序(gd 轮询观测,Handoff 时一次性导入):每项一条 Prelude span,
    /// 相邻观测差分 ≈ 单项耗时(含 Steam 异步查询;0.1s 轮询量化)。
    /// startMsec 为 gd 引擎毫秒(与 ToEngineMs 同一时间轴,可直接作 StartMs)。
    /// endMs ≤ 0 表示 gd 未观测到结束标记,以当前引擎时刻兜底。不推进进度条。
    /// </summary>
    internal void RecordWorkshopScan(List<(string Id, string Name, double StartMs)> entries, double endMs)
    {
        if (Frozen || entries.Count == 0) return;
        if (endMs <= 0) endMs = _engineMsec();
        lock (WorkshopSpans)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                double end = i + 1 < entries.Count ? entries[i + 1].StartMs : endMs;
                WorkshopSpans.Add(new Api.LoadSpan(
                    "workshop " + entries[i].Id, Api.LoadPhase.Prelude,
                    entries[i].StartMs, Math.Max(0, end - entries[i].StartMs),
                    entries[i].Name));
            }
        }
    }

    /// <summary>
    /// mod 加载内部子步骤(初始化器/资源包挂载):记 span(Id = 所属 mod id),
    /// 只进 Api 数据与瀑布图,不推进进度条(活动日志文案由钩子另行 Activity)。
    /// </summary>
    internal void ModSubStep(string detail, long startTicks, long endTicks)
    {
        if (Frozen) return;
        lock (SubStepSpans)
        {
            SubStepSpans.Add(new Api.LoadSpan(
                _currentModId ?? "?", Api.LoadPhase.ModSubStep,
                ToEngineMs(startTicks), (endTicks - startTicks) * SwTicksToMs, detail));
        }
    }

    /// <summary>
    /// TryLoadMod postfix:记 span、推进计数与 0.25+0.35·n/t;末个 mod 完成 0.60 + phase.mod_load。
    /// 计数依赖的文案走 Func&lt;int,string&gt;(计数在本类,文案在钩子——I18n 不进时间线)。
    /// 返回递增后的计数(钩子日志用)。
    /// </summary>
    internal int ModLoaded(string id, string state, Func<int, string> stepText, string detail,
        string doneStepText, Func<int, string> doneDetail)
    {
        if (Frozen) return Count;
        long nowTicks = _swTicks();
        _lastModTicks = nowTicks;
        Count++;
        // 耗时记录(prefix→postfix 真实区间,亚毫秒精度)
        lock (ModSpans)
        {
            ModSpans.Add(new Api.LoadSpan(
                id, Api.LoadPhase.ModLoad,
                ToEngineMs(_modStartTicks), (nowTicks - _modStartTicks) * SwTicksToMs, state));
        }

        float local = Count / (float)Total;
        // postfix 只更新状态；下一个 prefix 会把结果与下一个 mod 名一起提交，
        // 从而把同步突发期的强制绘制次数减半。最后一个 mod 仍在下方强制提交。
        Present(BootStage.Mods, WorkshopEnd + (ModsEnd - WorkshopEnd) * local, local,
            stepText(Count), detail, false);

        if (Count >= Total && !ModsDone)
        {
            ModsDone = true;
            if (_firstModTicks >= 0)
            {
                lock (PhaseSpans)
                {
                    PhaseSpans.Add(new Api.LoadSpan(
                        "phase.mod_load", Api.LoadPhase.ModLoad,
                        ToEngineMs(_firstModTicks), (_lastModTicks - _firstModTicks) * SwTicksToMs,
                        $"{Count} mods"));
                }
            }
            // mod 段结束 → 首个 Essential 步骤之间的收尾(mod 列表重建/对象池/
            // Essential 前导)开段,由 StepStarted 关闭
            OpenBoundary("wp.postMods", nowTicks);
            Present(BootStage.Mods, ModsEnd, 1f, doneStepText, doneDetail(Count), true);
        }
        return Count;
    }

    /// <summary>启动子步骤 prefix:查分数表、相邻差分收尾上一 step span。未知键忽略。</summary>
    internal void StepStarted(string labelKey, string stepText, string detail)
    {
        if (Frozen) return;
        if (!StepFractions.TryGetValue(labelKey, out var progress)) return;
        long nowTicks = _swTicks();
        CloseBoundary(nowTicks);
        ClosePendingStep(nowTicks);
        _lastStepTicks = nowTicks;
        lock (StepSpans)
        {
            StepSpans.Add(new Api.LoadSpan(labelKey, Api.LoadPhase.BootStep, ToEngineMs(nowTicks), 0, ""));
        }
        Present(BootStage.Essential, progress.Overall, progress.Local, stepText, detail, true);
    }

    /// <summary>ExecuteEssential postfix:准确收尾最后一个同步步骤，不把后续资产/Logo 时间算进 InitIds。</summary>
    internal void EssentialCompleted()
    {
        if (Frozen) return;
        ClosePendingStep(_swTicks());
        _lastStepTicks = -1;
        OpenBoundary("wp.cloudSave", _swTicks());
        Present(BootStage.Essential, EssentialEnd, 1f, null, null, true);
    }

    /// <summary>钩子先用它过滤:未知会话/已冻结时跳过反射读(游戏内房间会话每帧都来,必须廉价)。</summary>
    internal bool TracksSession(string name) => !Frozen && SessionRanges.ContainsKey(name);

    /// <summary>
    /// 资产会话 postfix:会话子区间插值 + 完成时记 span。计数文案走 Func&lt;int,string&gt;(stat.Total 在本类)。
    /// </summary>
    internal void SessionAdvanced(object session, string name, int loaded, int remaining,
        string? currentItem, string stepText, Func<int, string?, string> detail)
    {
        if (Frozen) return;
        if (!SessionRanges.TryGetValue(name, out var range)) return;

        long nowTicks = _swTicks();
        SessionStat stat = _sessionStats.GetValue(session, _ => new SessionStat());
        if (!string.IsNullOrEmpty(currentItem)) stat.CurrentItem = currentItem;
        if (stat.Total <= 0)
        {
            stat.Total = loaded + remaining;
            stat.FirstTicks = nowTicks;
        }
        stat.LastTicks = nowTicks;
        if (stat.Total > 0 && remaining == 0 && !stat.Recorded)
        {
            stat.Recorded = true;
            lock (SessionSpans)
            {
                SessionSpans.Add(new Api.LoadSpan(
                    name, Api.LoadPhase.AssetSession,
                    ToEngineMs(stat.FirstTicks), (stat.LastTicks - stat.FirstTicks) * SwTicksToMs,
                    $"{loaded}/{stat.Total}"));
            }
        }

        // 除零防护:会话首见时可能 loaded 与 remaining 同时为 0(资产批量
        // 加载失败时会静默丢弃非 Ok 的请求、一调用内清空完成)→ 0/0 = NaN。
        // 空会话按已完成处理。
        float local = stat.Total > 0 ? 1f - remaining / (float)stat.Total : 1f;
        Present(range.Stage, range.Start + (range.End - range.Start) * local, local,
            stepText, detail(stat.Total, stat.CurrentItem), false);
    }

    /// <summary>
    /// 路标:Logo=0.82 / MenuLoad=0.88 / MainMenu=0.66(仅首次生效)。
    /// 相邻边界差分记 Transition span,覆盖 step/会话之外的启动段(云同步+读档、
    /// 开场画面入场、开场动画、主菜单场景加载),瀑布图尾段不留观测空洞。
    /// skipLogo 启动无 Logo 路标,段相应合并。
    /// </summary>
    internal void Waypoint(BootWaypoint w, string stepText, string detail)
    {
        if (Frozen) return;
        if (w == BootWaypoint.MainMenu)
        {
            if (_menuHandled) return;
            _menuHandled = true;
        }
        long nowTicks = _swTicks();
        CloseBoundary(nowTicks);
        OpenBoundary(w switch
        {
            BootWaypoint.MainMenu => "wp.preLogo",
            BootWaypoint.Logo => "wp.logo",
            _ => "wp.menuScene",
        }, nowTicks);
        BootStage stage = w switch
        {
            BootWaypoint.MainMenu => BootStage.OpeningAssets,
            BootWaypoint.Logo => BootStage.Intro,
            _ => BootStage.Menu,
        };
        Present(stage, WaypointFractions[w], -1f, stepText, detail, true);
    }

    /// <summary>gd splash 交接(BootSplash.Handoff):引擎启动锚点 + 前奏阶段 spans。</summary>
    internal void SetBootAnchor(long anchorMsec)
    {
        _bootAnchorMsec = anchorMsec;
        long nowMsec = _engineMsec();
        if (_bootAnchorMsec >= 0)
        {
            lock (PhaseSpans)
            {
                PhaseSpans.Add(new Api.LoadSpan(
                    "phase.prelude", Api.LoadPhase.Prelude,
                    _bootAnchorMsec, nowMsec - _bootAnchorMsec, ""));
                PhaseSpans.Add(new Api.LoadSpan(
                    "phase.engine_init", Api.LoadPhase.Prelude,
                    0, _bootAnchorMsec, ""));
            }
        }
    }

    /// <summary>主菜单已显示(ExecuteDeferred 语义):收尾末步骤 span、TotalBootMs(含首次启动兜底)、冻结、呈现 1.0。</summary>
    internal void MenuReady(string doneText, string detail)
    {
        if (Frozen) return;
        ClosePendingStep(_swTicks());
        CloseBoundary(_swTicks());

        if (_bootAnchorMsec >= 0)
        {
            _totalBootMs = _engineMsec() - _bootAnchorMsec;
        }
        else
        {
            // 首次启动兜底:注入发生在 mod 加载期、autoload 已解析完,gd 节点
            // 不存在 → 锚点=-1。兜底用 0 锚点:TotalBootMs=引擎至今总时长;span 的
            // StartMs 本就是绝对引擎毫秒,÷total 的分数定位自然正确(prelude 段无数据)。
            _totalBootMs = _engineMsec();
        }
        Frozen = true;
        Present(BootStage.Menu, 1.0f, 1f, doneText, detail, true);
    }

    /// <summary>一行启动摘要(读 spans 的诊断文本);无 mod 数据时返回 null(编排层免记日志)。</summary>
    internal string? BootSummary()
    {
        if (ModSpans.Count == 0) return null;
        var top = new StringBuilder("[ItsLoading] boot ");
        top.Append(_totalBootMs.ToString("F0")).Append("ms")
           .Append($" (prefix={PrefixCalls} postfix={ModSpans.Count})")
           .Append("; slowest mods:");
        foreach (var s in ModSpans
                     .OrderByDescending(m => m.DurationMs)
                     .ThenBy(m => m.Id, StringComparer.Ordinal)
                     .Take(3))
        {
            top.Append(' ').Append(s.Id).Append('=').Append(s.DurationMs.ToString("F0")).Append("ms");
        }
        return top.ToString();
    }

    private void OpenBoundary(string labelKey, long startTicks)
    {
        _boundaryTicks = startTicks;
        _boundaryLabel = labelKey;
    }

    private void CloseBoundary(long endTicks)
    {
        if (_boundaryTicks < 0 || _boundaryLabel == null) return;
        lock (WaypointSpans)
        {
            WaypointSpans.Add(new Api.LoadSpan(
                _boundaryLabel, Api.LoadPhase.Transition,
                ToEngineMs(_boundaryTicks), (endTicks - _boundaryTicks) * SwTicksToMs, ""));
        }
        _boundaryTicks = -1;
        _boundaryLabel = null;
    }

    private void ClosePendingStep(long nowTicks)
    {
        lock (StepSpans)
        {
            if (StepSpans.Count == 0 || _lastStepTicks < 0) return;
            var prev = StepSpans[^1];
            StepSpans[^1] = prev with
            {
                DurationMs = (nowTicks - _lastStepTicks) * SwTicksToMs,
            };
        }
    }
}
