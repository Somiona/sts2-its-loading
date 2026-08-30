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
        ModLoad,       // 单个 mod 的 TryLoadMod 全程
        ModSubStep,    // mod 加载内部子步骤(初始化器执行 / 资源包挂载)
        BootStep,      // Essential 启动子步骤
        AssetSession,  // 资产加载会话(按会话聚合)
        Transition,    // 路标段(云同步+读档 / 开场动画 / 主菜单场景等 step 与会话之外的启动段)
    }

    /// <summary>
    /// 只读查询:启动过程中每个环节的真实加载时长。
    /// 启动完成(主菜单就绪)后数据冻结;冻结前查询返回当时快照。
    /// 全部数据常驻 &lt;10KB,不写磁盘;数据由 BootTimeline(启动时间线)写入,
    /// 记录动作复用进度条已有的钩子,无额外 patch。
    /// </summary>
    public static class LoadingDurations
    {
        private static LoadSpan[] _frozenMods, _frozenSubSteps, _frozenWorkshop, _frozenSteps, _frozenSessions, _frozenPhases, _frozenWaypoints;

        private static BootTimeline T => ItsLoading.Timeline;

        /// <summary>启动是否已完成、数据是否已冻结。</summary>
        public static bool IsReady => T?.Frozen ?? false;

        /// <summary>逐 mod 加载耗时(Id = mod id,Detail = 加载状态)。本 mod 自身不计。</summary>
        public static IReadOnlyList<LoadSpan> ModLoads =>
            Snapshot(T?.ModSpans, ref _frozenMods);

        /// <summary>
        /// 工坊扫描逐项耗时(Id = "workshop 工坊项 id",Detail = mod 显示名)。
        /// gd 在帧 0 起轮询日志观测,相邻观测差分 ≈ 单项耗时(0.1s 量化,含 Steam 查询);
        /// 首次安装启动(脚本未就绪)或扫描早于 gd 观测时本表为空。
        /// </summary>
        public static IReadOnlyList<LoadSpan> WorkshopItems =>
            Snapshot(T?.WorkshopSpans, ref _frozenWorkshop);

        /// <summary>
        /// mod 加载内部子步骤(Id = 所属 mod id,Detail = "init 类型名" / "pck 文件名")。
        /// 覆盖 TryLoadMod 内的可挂钩点:初始化器执行(耗时大头)与资源包挂载;
        /// 程序集加载是 BCL 方法,不单独计时(时间差含在所属 mod 的总 span 里)。
        /// 工坊读取的内部步骤发生在 C# 之前,不在本表(见 Prelude)。
        /// </summary>
        public static IReadOnlyList<LoadSpan> ModSubSteps =>
            Snapshot(T?.SubStepSpans, ref _frozenSubSteps);

        /// <summary>启动子步骤(图集/本地化/模型库等)。</summary>
        public static IReadOnlyList<LoadSpan> BootSteps =>
            Snapshot(T?.StepSpans, ref _frozenSteps);

        /// <summary>资产加载会话(Detail = loaded/total)。</summary>
        public static IReadOnlyList<LoadSpan> AssetSessions =>
            Snapshot(T?.SessionSpans, ref _frozenSessions);

        /// <summary>路标段:Essential 完成后相邻路标之间的区段(云同步+读档、
        /// 开场画面入场、开场动画、主菜单场景加载)。skipLogo 启动会跳过 Logo 路标,
        /// 段相应合并。</summary>
        public static IReadOnlyList<LoadSpan> Waypoints =>
            Snapshot(T?.WaypointSpans, ref _frozenWaypoints);

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
