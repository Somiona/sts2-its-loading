using System;
using System.Collections.Generic;
using System.Diagnostics;

#nullable enable

namespace ItsLoading;

/// <summary>
/// 加载屏的唯一视图模型(2026-09-02 收敛):把 LoadingViewState 加工成两个
/// 渲染器(gd interpreter / 原生呈现面)共用的呈现快照 —— 阶段文本包装、
/// 活动日志环、不定相位时钟。此前这套逻辑在 boot.gd 与 FreezeScreen 各有
/// 一份且已漂移(native 文案缺阶段包装、日志缺前奏行),现为单一事实源。
///
/// 数据流:BootTimeline(模型)→ 本类(视图模型)→ 双渲染器(视图)。
/// 前奏行(工坊扫描)只有帧 0 的 boot.gd 轮询得到,桥 attach 后经
/// get_workshop_log 上交,SeedLog 灌入本环(前插,保证历史在前)。
/// 纯 BCL:文本格式化经注入,离线可单测。
/// </summary>
internal sealed class LoadingPresentation
{
    /// <summary>与 boot.gd 的 LOG_STREAM_CAP 配对(窗口淘汰归各渲染器的 LogWindow)。</summary>
    private const int LogCap = 60;

    private readonly Func<int, string, string> _stageText;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _log = new();
    private string _lastLine = "";
    private int _lastStage;
    private string _lastDetail = "";

    internal LoadingPresentation(Func<int, string, string>? stageText = null)
    {
        _stageText = stageText ?? ((stage, step) => step);
    }

    /// <summary>呈现一次(在 BootTimeline.Presenter 组合点调用,先于双渲染器)。</summary>
    internal PresentedSnapshot Present(LoadingViewState state)
    {
        bool stageChanged = (int)state.Stage != _lastStage;
        if (stageChanged) _lastStage = (int)state.Stage;
        UpdateLog(state, stageChanged);
        return new PresentedSnapshot(
            state.Stage, state.Overall, state.Local, state.LocalIndeterminate,
            _clock.Elapsed.TotalSeconds, stageChanged,
            _stageText((int)state.Stage, state.Step ?? ""), state.Detail ?? "", _log);
    }

    /// <summary>灌入 gd 前奏日志(boot.gd 轮询所得,经 BootSplash.Handoff 上交)。
    /// 前插语义:历史永远在最前,与本类后续追加的里程碑行正确排序,
    /// 且与调用时机(Handoff 早于/晚于首次 Present)无关。</summary>
    internal void SeedLog(IEnumerable<string> preludeLines)
    {
        _log.InsertRange(0, preludeLines);
        Trim();
    }

    private void UpdateLog(LoadingViewState state, bool stageChanged)
    {
        // 镜像原 boot.gd _log_line 语义:阶段切换记里程碑;否则 detail 变化记行
        // (裸「+ms」计时行带上步骤名);连续相同只记一次
        string line = "";
        if (stageChanged)
        {
            line = state.Step ?? "";
            _lastDetail = "";
        }
        else if (state.Detail is { Length: > 0 } d && d != _lastDetail)
        {
            line = d.StartsWith('+') ? $"{state.Step} {d}" : d;
        }
        if (line.Length == 0 || line == _lastLine) return;
        _lastLine = line;
        _lastDetail = state.Detail ?? "";
        _log.Add(line);
        Trim();
    }

    private void Trim()
    {
        if (_log.Count > LogCap) _log.RemoveRange(0, _log.Count - LogCap);
    }
}

/// <summary>双渲染器共用的呈现快照。StepText 已含阶段包装(与 gd 旧输出逐字节一致)。</summary>
internal readonly record struct PresentedSnapshot(
    BootStage Stage,
    float Overall,
    float Local,
    bool LocalIndeterminate,
    double T,
    bool StageChanged,
    string StepText,
    string DetailText,
    IReadOnlyList<string> Log);
