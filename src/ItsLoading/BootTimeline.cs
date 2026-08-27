using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
#nullable enable

namespace ItsLoading;

// ---------------------------------------------------------------- 启动时间线(架构拆分 #3 的深模块)
//
// 单一写缝:Harmony 钩子只报事实(mod/步骤/会话/路标),本类拥有——
//   · 0→1 进度刻度表(Steps 分数、SessionRanges 子区间、路标分数)——唯一真源
//   · 全部 span 记录(Api.LoadingDurations 是它的只读查询面)
//   · 锚点与时钟换算(EngineOffset、BootAnchor、TotalBootMs)
//   · 冻结语义(菜单就绪后数据封存)
// 呈现为推模型:每个事件回调一次 Presenter——这也是将来主题缝(#7)的时钟
// (诚实动画:Present 调用密度 = 真实加载活动密度,见 CONTEXT.md)。
// 文案由钩子解析后传入(I18n 不进本类);纯 BCL、双时钟注入 → 可离线单测(tests/)。

/// <summary>启动路标(原先散落各钩子里的 0.82/0.88/0.66 收敛于此)。</summary>
internal enum BootWaypoint { Logo, MenuLoad, MainMenu }

internal sealed class BootTimeline
{
    // ---- 刻度表(单一进度刻度 0→1:工坊 0-0.25 / mod 加载 0.25-0.60 / Essential 0.60-0.88 / 菜单就绪 0.88-1.0) ----

    /// <summary>启动子步骤键 → 分数。ItsLoading 侧只保留 patch 定位(Type/Method),分数真源在此。</summary>
    private static readonly Dictionary<string, float> StepFractions = new()
    {
        { "step.atlas", 0.615f },
        { "step.loc", 0.625f },
        { "step.modeldb", 0.635f },
        { "step.ids", 0.645f },
        { "step.preload", 0.655f },
    };

    /// <summary>
    /// 启动期的会话名 → 单条进度刻度上的子区间。游戏内会话(房间/角色)不在此表,自动忽略。
    /// 注意没有 "MainMenu" 会话:游戏唯一创建它的 PreloadManager.LoadMainMenuAssets 零调用者,
    /// 菜单资产实际由 "Common" 会话加载(LoadCommonAndMainMenuAssets),但那发生在
    /// ExecuteDeferred(=条的 1.0 完成点与 Freeze 点)之后、条 2 秒弥留期内——若映射它,
    /// 会把已显示 1.0「完成」的条拽回 0.88。启动边界定在菜单就绪,延迟资产
    /// 属启动后后台工作,不进条也不进冻结的 Api 数据(todo#4,2026-08-27)。
    /// </summary>
    private static readonly Dictionary<string, (float Start, float End)> SessionRanges = new()
    {
        { "IntroLogo", (0.66f, 0.70f) },
        { "MainMenuEssentials", (0.70f, 0.82f) },
    };

    private static readonly Dictionary<BootWaypoint, float> WaypointFractions = new()
    {
        { BootWaypoint.Logo, 0.82f },
        { BootWaypoint.MenuLoad, 0.88f },
        { BootWaypoint.MainMenu, 0.66f },
    };

    // ---- 状态(原 Recorder 全部并入;主线程独占写入) ----

    /// <summary>呈现回调(推模型):(frac, step, detail, forceDraw) → SetProgress;step/detail 可为 null(不动文案)。</summary>
    public Action<float, string?, string?, bool>? Presenter;

    private readonly Func<long> _engineMsec;
    private readonly Func<long> _swTicks;
    private readonly double _engineOffsetMs; // C# Stopwatch 时间轴 → 引擎时间轴(gd 第 0 帧起算)

    internal bool Frozen { get; private set; }
    internal bool ModsDone { get; private set; }
    internal int Count { get; private set; } = 1; // 本 mod 自己算第 1 个
    internal int Total { get; private set; } = 1;
    internal int PrefixCalls;                    // 诊断:prefix 实际触发次数
    internal double TotalBootMs => _totalBootMs;

    private double _totalBootMs = -1;
    private long _bootAnchorMsec = -1;
    private long _modStartTicks, _firstModTicks = -1, _lastModTicks, _lastStepTicks = -1;
    private bool _menuHandled;
    private float _frac = 0.25f;

    internal readonly List<Api.LoadSpan> ModSpans = new(capacity: 64);
    internal readonly List<Api.LoadSpan> StepSpans = new(capacity: 16);
    internal readonly List<Api.LoadSpan> SessionSpans = new(capacity: 8);
    internal readonly List<Api.LoadSpan> PhaseSpans = new(capacity: 8);

    // ConditionalWeakTable:session 结束后可被 GC,不阻止回收;值对象复用,无 per-frame 分配
    private readonly ConditionalWeakTable<object, SessionStat> _sessionStats = new();

    private sealed class SessionStat
    {
        public int Total;
        public long FirstTicks, LastTicks;
        public bool Recorded;
    }

    public BootTimeline(Func<long> engineMsec, Func<long> swTicks)
    {
        _engineMsec = engineMsec;
        _swTicks = swTicks;
        // 时钟对表(原 Init 显式步骤,现随构造;必须先于任何钩子的 ToEngineMs)
        _engineOffsetMs = engineMsec() - swTicks() * SwTicksToMs;
    }

    private static double SwTicksToMs => 1000.0 / Stopwatch.Frequency;
    private double ToEngineMs(long swTicks) => swTicks * SwTicksToMs + _engineOffsetMs;

    private void Present(float frac, string? step, string? detail, bool forceDraw)
    {
        _frac = frac;
        Presenter?.Invoke(frac, step, detail, forceDraw);
    }

    // ---- 写缝:钩子报事实 ----

    /// <summary>mod 段开始(Init 末):呈现 0.25 起点。</summary>
    internal void BeginMods(int total, string stepText)
    {
        Total = Math.Max(1, total);
        Present(0.25f, stepText, "", true);
    }

    /// <summary>TryLoadMod prefix:记 mod 段起点;present 同分数触发补画(presenter 内 ForceDraw)。</summary>
    internal void ModStarted()
    {
        PrefixCalls++;
        _modStartTicks = _swTicks();
        if (_firstModTicks < 0) _firstModTicks = _modStartTicks;
        // 突发期前缀补画:压缩"首次有效出帧"前的盲区(同 first paint 的冗余提交理由)
        Presenter?.Invoke(_frac, null, null, true);
    }

    /// <summary>
    /// TryLoadMod postfix:记 span、推进计数与 0.25+0.35·n/t;末个 mod 完成 0.60 + phase.mod_load。
    /// 计数依赖的文案走 Func&lt;int,string&gt;(计数在本类,文案在钩子——I18n 不进时间线)。
    /// 返回递增后的计数(钩子日志用)。
    /// </summary>
    internal int ModLoaded(string id, string state, Func<int, string> stepText, string detail,
        string doneStepText, Func<int, string> doneDetail)
    {
        long nowTicks = _swTicks();
        _lastModTicks = nowTicks;
        Count++;
        // 耗时记录(prefix→postfix 真实区间,亚毫秒精度)
        ModSpans.Add(new Api.LoadSpan(
            id, Api.LoadPhase.ModLoad,
            ToEngineMs(_modStartTicks), (nowTicks - _modStartTicks) * SwTicksToMs, state));

        Present(0.25f + 0.35f * (Count / (float)Total), stepText(Count), detail, true);

        if (Count >= Total && !ModsDone)
        {
            ModsDone = true;
            PhaseSpans.Add(new Api.LoadSpan(
                "phase.mod_load", Api.LoadPhase.ModLoad,
                ToEngineMs(_firstModTicks), (_lastModTicks - _firstModTicks) * SwTicksToMs,
                $"{Count} mods"));
            Present(0.60f, doneStepText, doneDetail(Count), true);
        }
        return Count;
    }

    /// <summary>启动子步骤 prefix:查分数表、相邻差分收尾上一 step span。未知键忽略(对齐原 StepMap 早退)。</summary>
    internal void StepStarted(string labelKey, string stepText, string detail)
    {
        if (!StepFractions.TryGetValue(labelKey, out var frac)) return;
        long nowTicks = _swTicks();
        ClosePendingStep(nowTicks);
        _lastStepTicks = nowTicks;
        StepSpans.Add(new Api.LoadSpan(labelKey, Api.LoadPhase.BootStep, ToEngineMs(nowTicks), 0, ""));
        Present(frac, stepText, detail, true);
    }

    /// <summary>钩子先用它过滤:未知会话/已冻结时跳过反射读(游戏内房间会话每帧都来,必须廉价)。</summary>
    internal bool TracksSession(string name) => !Frozen && SessionRanges.ContainsKey(name);

    /// <summary>
    /// 资产会话 postfix:会话子区间插值 + 完成时记 span。计数文案走 Func&lt;int,string&gt;(stat.Total 在本类)。
    /// </summary>
    internal void SessionAdvanced(object session, string name, int loaded, int remaining,
        string stepText, Func<int, string> detail)
    {
        if (Frozen) return;
        if (!SessionRanges.TryGetValue(name, out var range)) return;

        long nowTicks = _swTicks();
        SessionStat stat = _sessionStats.GetValue(session, _ => new SessionStat());
        if (stat.Total <= 0)
        {
            stat.Total = loaded + remaining;
            stat.FirstTicks = nowTicks;
        }
        stat.LastTicks = nowTicks;
        if (stat.Total > 0 && remaining == 0 && !stat.Recorded)
        {
            stat.Recorded = true;
            SessionSpans.Add(new Api.LoadSpan(
                name, Api.LoadPhase.AssetSession,
                ToEngineMs(stat.FirstTicks), (stat.LastTicks - stat.FirstTicks) * SwTicksToMs,
                $"{loaded}/{stat.Total}"));
        }

        // 除零防护(todo#5):会话首见时可能 loaded 与 remaining 同时为 0(资产批量
        // 加载失败时会静默丢弃非 Ok 的请求、一调用内清空完成)→ 0/0 = NaN。
        // 空会话按已完成处理。
        float local = stat.Total > 0 ? 1f - remaining / (float)stat.Total : 1f;
        Present(range.Start + (range.End - range.Start) * local, stepText, detail(stat.Total), false);
    }

    /// <summary>路标:Logo=0.82 / MenuLoad=0.88 / MainMenu=0.66(仅首次生效)。</summary>
    internal void Waypoint(BootWaypoint w, string stepText, string detail)
    {
        if (w == BootWaypoint.MainMenu)
        {
            if (_menuHandled) return;
            _menuHandled = true;
        }
        Present(WaypointFractions[w], stepText, detail, true);
    }

    /// <summary>gd splash 交接(BootSplash.Handoff):引擎启动锚点 + 前奏阶段 spans。</summary>
    internal void SetBootAnchor(long anchorMsec)
    {
        _bootAnchorMsec = anchorMsec;
        long nowMsec = _engineMsec();
        if (_bootAnchorMsec >= 0)
        {
            PhaseSpans.Add(new Api.LoadSpan(
                "phase.prelude", Api.LoadPhase.Prelude,
                _bootAnchorMsec, nowMsec - _bootAnchorMsec, ""));
            PhaseSpans.Add(new Api.LoadSpan(
                "phase.engine_init", Api.LoadPhase.Prelude,
                0, _bootAnchorMsec, ""));
        }
    }

    /// <summary>主菜单已显示(ExecuteDeferred 语义):收尾末步骤 span、TotalBootMs(含首启兜底)、冻结、呈现 1.0。</summary>
    internal void MenuReady(string doneText, string detail)
    {
        ClosePendingStep(_swTicks());

        if (_bootAnchorMsec >= 0)
        {
            _totalBootMs = _engineMsec() - _bootAnchorMsec;
        }
        else
        {
            // 首启兜底(todo#6):注入发生在 mod 加载期、autoload 已解析完,gd 节点
            // 不存在 → 锚点=-1。兜底用 0 锚点:TotalBootMs=引擎至今总时长;span 的
            // StartMs 本就是绝对引擎毫秒,÷total 的分数定位自然正确(prelude 段无数据)。
            _totalBootMs = _engineMsec();
        }
        Frozen = true;
        Present(1.0f, doneText, detail, true);
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

    private void ClosePendingStep(long nowTicks)
    {
        if (StepSpans.Count > 0 && _lastStepTicks >= 0)
        {
            var prev = StepSpans[^1];
            StepSpans[^1] = prev with
            {
                DurationMs = (nowTicks - _lastStepTicks) * SwTicksToMs,
            };
        }
    }
}
