using System;
using System.Collections.Generic;

namespace ItsLoading
{
    /// <summary>内部记录器:只在进度条已有钩子里被调用,主线程独占写入。</summary>
    internal static class Recorder
    {
        internal static readonly List<Api.LoadSpan> ModSpans = new(capacity: 64);
        internal static readonly List<Api.LoadSpan> StepSpans = new(capacity: 16);
        internal static readonly List<Api.LoadSpan> SessionSpans = new(capacity: 8);
        internal static readonly List<Api.LoadSpan> PhaseSpans = new(capacity: 8);

        /// <summary>C# 时间轴(Stopwatch)→ 引擎时间轴的换算偏移;Init 时对表。</summary>
        internal static double EngineOffsetMs;

        /// <summary>引擎启动锚点(gd 第 0 帧的 Time.get_ticks_msec),-1 = 无 gd。</summary>
        internal static long BootAnchorMsec = -1;

        internal static double TotalBootMs = -1;

        // —— 供钩子使用的轻量状态(无分配热路径)——
        internal static int PrefixCalls;         // 诊断:prefix 实际触发次数
        internal static long ModStartTicks;      // TryLoadMod prefix 记录
        internal static long FirstModTicks = -1; // mod 段总区间
        internal static long LastModTicks;
        internal static long LastStepTicks = -1; // 上一个步骤的起点(算相邻差)
        internal static double SwTicksToMs => 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        internal static double ToEngineMs(long swTicks) => swTicks * SwTicksToMs + EngineOffsetMs;
    }
}

namespace ItsLoading.Api
{
    /// <summary>一次加载的耗时区间。时间轴原点 = 引擎启动(gd 第 0 帧)。</summary>
    public readonly record struct LoadSpan(
        string Id,
        LoadPhase Phase,
        double StartMs,
        double DurationMs,
        string Detail);

    public enum LoadPhase
    {
        Prelude,       // 引擎启动 + 工坊读取(到第一个 mod 加载前)
        ModLoad,       // 单个 mod 的 TryLoadMod(含其初始化器)
        BootStep,      // Essential 启动子步骤
        AssetSession,  // 资产加载会话(按会话聚合)
    }

    /// <summary>
    /// 只读查询:启动过程中每个环节的真实加载时长。
    /// 启动完成(主菜单就绪)后数据冻结;冻结前查询返回当时快照。
    /// 全部数据常驻 &lt;10KB,不写磁盘;记录动作复用进度条已有的钩子,无额外 patch。
    /// </summary>
    public static class LoadingDurations
    {
        private static volatile bool _frozen;
        private static LoadSpan[] _frozenMods, _frozenSteps, _frozenSessions, _frozenPhases;

        /// <summary>启动是否已完成、数据是否已冻结。</summary>
        public static bool IsReady => _frozen;

        /// <summary>逐 mod 加载耗时(Id = mod id,Detail = 加载状态)。本 mod 自身不计。</summary>
        public static IReadOnlyList<LoadSpan> ModLoads =>
            Snapshot(Recorder.ModSpans, ref _frozenMods);

        /// <summary>启动子步骤(图集/本地化/模型库等)。</summary>
        public static IReadOnlyList<LoadSpan> BootSteps =>
            Snapshot(Recorder.StepSpans, ref _frozenSteps);

        /// <summary>资产加载会话(Detail = loaded/total)。</summary>
        public static IReadOnlyList<LoadSpan> AssetSessions =>
            Snapshot(Recorder.SessionSpans, ref _frozenSessions);

        /// <summary>粗粒度阶段(前奏 / mod 加载总段)。</summary>
        public static IReadOnlyList<LoadSpan> Phases =>
            Snapshot(Recorder.PhaseSpans, ref _frozenPhases);

        /// <summary>引擎启动(gd 第 0 帧)到主菜单就绪的总毫秒数;-1 表示尚未就绪。</summary>
        public static double TotalBootMs => Recorder.TotalBootMs;

        private static LoadSpan[] Snapshot(List<LoadSpan> src, ref LoadSpan[] cache)
        {
            if (_frozen)
            {
                return cache ??= src.ToArray();
            }
            lock (src)
            {
                return src.ToArray();
            }
        }

        internal static void Freeze()
        {
            _frozen = true;
        }
    }
}
