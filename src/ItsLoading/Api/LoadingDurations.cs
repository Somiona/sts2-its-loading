using System;
using System.Collections.Generic;

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
    /// 全部数据常驻 &lt;10KB,不写磁盘;数据由 BootTimeline(启动时间线)写入,
    /// 记录动作复用进度条已有的钩子,无额外 patch。
    /// </summary>
    public static class LoadingDurations
    {
        private static LoadSpan[] _frozenMods, _frozenSteps, _frozenSessions, _frozenPhases;

        private static BootTimeline T => ItsLoading.Timeline;

        /// <summary>启动是否已完成、数据是否已冻结。</summary>
        public static bool IsReady => T?.Frozen ?? false;

        /// <summary>逐 mod 加载耗时(Id = mod id,Detail = 加载状态)。本 mod 自身不计。</summary>
        public static IReadOnlyList<LoadSpan> ModLoads =>
            Snapshot(T?.ModSpans, ref _frozenMods);

        /// <summary>启动子步骤(图集/本地化/模型库等)。</summary>
        public static IReadOnlyList<LoadSpan> BootSteps =>
            Snapshot(T?.StepSpans, ref _frozenSteps);

        /// <summary>资产加载会话(Detail = loaded/total)。</summary>
        public static IReadOnlyList<LoadSpan> AssetSessions =>
            Snapshot(T?.SessionSpans, ref _frozenSessions);

        /// <summary>粗粒度阶段(前奏 / mod 加载总段)。</summary>
        public static IReadOnlyList<LoadSpan> Phases =>
            Snapshot(T?.PhaseSpans, ref _frozenPhases);

        /// <summary>引擎启动(gd 第 0 帧)到主菜单就绪的总毫秒数;-1 表示尚未就绪。</summary>
        public static double TotalBootMs => T?.TotalBootMs ?? -1;

        private static LoadSpan[] Snapshot(List<LoadSpan> src, ref LoadSpan[] cache)
        {
            if (src == null) return Array.Empty<LoadSpan>();
            if (IsReady)
            {
                return cache ??= src.ToArray();
            }
            lock (src)
            {
                return src.ToArray();
            }
        }
    }
}
