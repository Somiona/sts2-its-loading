using System;
using System.Collections.Generic;

namespace ItsLoading;

/// <summary>
/// 画廊预览的伪启动剧本(纯 BCL,离线可单测):模拟一次完整游戏启动 ——
/// 工坊扫描、逐 mod 加载明细、读存档、开场动画、主菜单,进度条一路跑到
/// 100% 后显示 Ready,停顿片刻从头循环。文案经注入的翻译器取 bar.* 表
/// (与真实启动同一张);Tick 是重放语义:事件按时刻累积,单次 Tick(大 t)
/// 即得与连续播放完全一致的中间状态 —— 静态缩略图与 hover 实况同源。
/// </summary>
internal sealed class BootScript
{
    /// <summary>剧本段:(时长秒, 阶段名键, overall 终点)。末段是 Ready 停顿
    /// (overall 已满),阶段数 = 段数−1,与真实 LoadingViewState.StageCount=7
    /// 对齐。节奏对齐 tools/preview_boot.sh 的时间线剧本(全轮 ≈5s,不拖沓)。</summary>
    private static readonly (float Dur, string StageKey, float To)[] Plan =
    {
        (0.5f, "bar.starting", 0.08f),
        (0.7f, "bar.workshop", 0.22f),
        (1.4f, "bar.mods", 0.72f),
        (0.3f, "bar.readingProgress", 0.80f),
        (0.25f, "bar.readingPrefs", 0.86f),
        (0.6f, "bar.logo", 0.94f),
        (0.55f, "bar.menuIn", 1.00f),
        (0.7f, "bar.done", 1.00f),
    };
    internal const int StageCount = 7;

    /// <summary>静态取景点:mod 加载过半(overall≈50%,日志最热闹)。</summary>
    internal const double SnapshotAt = 2.0;

    /// <summary>伪 mod 列表(确定性假数据,含本 mod 自己)。</summary>
    private static readonly string[] Mods =
        { "BaseLib", "ItsLoading", "map-extensions", "quick-restart", "card-art-hd", "music-expansion" };

    /// <summary>theme_apply 的一帧快照(纯数据;Godot 侧负责映射成 Dictionary)。</summary>
    internal readonly record struct Snapshot(
        double Overall, float Local, bool Indeterminate, double T,
        int Stage, bool StageChanged, string Step, string Detail, IReadOnlyList<string> Log);

    /// <summary>剧本事件:(时刻, 明细行, 日志行, 当前 mod 序号)。null = 不动。</summary>
    private readonly record struct Ev(double Time, string Detail, string Log, int ModIndex);

    private readonly Func<string, Dictionary<string, string>, string> _t;
    private readonly List<Ev> _events = new();
    private readonly double _total;
    private double _elapsed;
    private Snapshot _current;
    private bool _hasCurrent;
    private double _prevCycle = -1;
    private int _lastStage;
    private int _emitted;
    private int _modIndex;
    private string _detail = "";
    private readonly List<string> _log = new();

    internal BootScript(Func<string, Dictionary<string, string>, string> translate)
    {
        _t = translate;
        foreach (var seg in Plan) _total += seg.Dur;
        BuildEvents();
    }

    /// <summary>剧本总时长(秒) = 各段之和(含末段 Ready 停顿)。</summary>
    internal double Total => _total;

    /// <summary>画廊 hover 时播放;false 时 Advance 保持当前帧。</summary>
    internal bool Playing { get; set; }

    internal Snapshot Seek(double t)
    {
        _elapsed = t;
        _current = Tick(t);
        _hasCurrent = true;
        return _current;
    }

    internal Snapshot Advance(double delta)
    {
        if (!_hasCurrent) return Seek(0);
        if (!Playing) return _current;
        _elapsed += delta;
        _current = Tick(_elapsed);
        return _current;
    }

    /// <summary>把剧本推到 t 秒。连续调用(递增 t)= 实时播放;单次调用(大 t)
    /// = 一次性重放,事件/日志与连续播放到同一时刻的结果一致。</summary>
    internal Snapshot Tick(double t)
    {
        double cyc = t % _total;
        if (cyc < _prevCycle)
        {
            // 循环回卷:状态归零,像一次全新启动
            _log.Clear();
            _emitted = 0;
            _modIndex = 0;
            _lastStage = 0;
            _detail = "";
        }
        _prevCycle = cyc;

        int seg = 0;
        double start = 0;
        while (seg < Plan.Length - 1 && cyc >= start + Plan[seg].Dur)
        {
            start += Plan[seg].Dur;
            seg++;
        }
        double frac = (cyc - start) / Plan[seg].Dur;
        frac = frac < 0 ? 0 : (frac > 1 ? 1 : frac);
        double from = seg == 0 ? 0.0 : Plan[seg - 1].To;
        int stage = Math.Min(seg + 1, StageCount);
        bool stageChanged = stage != _lastStage;
        _lastStage = stage;
        if (stageChanged) _detail = "";
        while (_emitted < _events.Count && _events[_emitted].Time <= cyc)
        {
            Ev e = _events[_emitted++];
            if (e.Log != null)
            {
                _log.Add(e.Log);
                if (_log.Count > 14) _log.RemoveAt(0);
            }
            if (e.Detail != null) _detail = e.Detail;
            if (e.ModIndex >= 0) _modIndex = e.ModIndex;
        }

        // 阶段标题与真实启动同式:Stage {n}/{t} · {name};mod 段计数随事件推进
        string name = seg == 2
            ? _t("bar.mods", new()
            {
                ["n"] = Math.Min(_modIndex + 1, Mods.Length).ToString(),
                ["t"] = Mods.Length.ToString(),
            })
            : _t(Plan[seg].StageKey, null);
        bool indeterminate = seg == 1; // 工坊扫描无总量:走主题的不定进度表现
        return new Snapshot(
            from + (Plan[seg].To - from) * frac,
            seg == Plan.Length - 1 ? 1.0f : (float)frac,
            indeterminate,
            cyc,
            stage,
            stageChanged,
            _t("bar.stage", new()
            {
                ["n"] = stage.ToString(),
                ["t"] = StageCount.ToString(),
                ["name"] = name,
            }),
            _detail,
            _log.ToArray());
    }

    // ---- 事件表:确定性假数据;参数格式与真实补丁一致("{n}ms" / 原始 id)----

    private void BuildEvents()
    {
        var starts = new double[Plan.Length];
        for (int i = 1; i < Plan.Length; i++) starts[i] = starts[i - 1] + Plan[i - 1].Dur;

        // 工坊扫描(段 2):逐工坊条目,同一条既做明细也进日志
        string[] workshop = { "waypoint", "colored-map", "backstab-pack" };
        for (int i = 0; i < workshop.Length; i++)
        {
            string s = _t("bar.workshopItem", new() { ["id"] = workshop[i] });
            _events.Add(new Ev(starts[1] + (i + 0.6) * Plan[1].Dur / (workshop.Length + 0.2), s, s, -1));
        }
        // mod 加载(段 3):开始→loading 明细,完成→modLoaded 日志;隔一个撒 pckMounted
        for (int i = 0; i < Mods.Length; i++)
        {
            double slot = Plan[2].Dur / Mods.Length;
            double at = starts[2] + i * slot + 0.05;
            int ms = 90 + i * 137 % 420;
            _events.Add(new Ev(at, _t("bar.loadingMod", new() { ["id"] = Mods[i] }), null, i));
            _events.Add(new Ev(at + slot * 0.75, null,
                _t("bar.modLoaded", new() { ["id"] = Mods[i], ["ms"] = $"{ms}ms" }), -1));
            if (i % 2 == 1)
                _events.Add(new Ev(at + slot * 0.4, null,
                    _t("bar.pckMounted", new() { ["name"] = Mods[i], ["ms"] = $"{ms / 2}ms" }), -1));
        }
        // 存档/偏好(段 4/5)
        _events.Add(new Ev(starts[3] + Plan[3].Dur * 0.8, null,
            _t("bar.readReady", new() { ["ms"] = "312ms" }), -1));
        _events.Add(new Ev(starts[4] + Plan[4].Dur * 0.8, null,
            _t("bar.prefsReady", new() { ["ms"] = "88ms" }), -1));
        _events.Sort((a, b) => a.Time.CompareTo(b.Time));
    }
}
