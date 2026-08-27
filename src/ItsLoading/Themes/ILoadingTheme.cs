using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 主题缝(架构拆分 #7)
//
// 主题 = 启动加载指示器的一种呈现,挂在 BootTimeline.Presenter 上:
//   Build   —— Init 里建 UI(直接挂 Root;同步突发期禁 Container/deferred)
//   Present —— 就是 Presenter 目标;调用密度 = 真实加载活动密度(诚实动画,见 CONTEXT.md)
//   Retire  —— 菜单就绪 + 2s 弥留后由编排层调用;gd splash 的 takeover 不归主题管
// 刻度数学、span 记录全部在 BootTimeline——主题只管"长什么样"。
// v1 边界:pre-C# 段(0→0.25)恒由 gd splash 以经典条外观呈现,不随主题变化;
// 将来若主题需要 frame 0 生效,再扩展 gd 侧渲染。

/// <summary>加载指示器主题接口。</summary>
#nullable enable
internal interface ILoadingTheme
{
    /// <summary>建立 UI(在 Init 的 build bar 步骤调用;此时 BootSplash.Install 已完成)。</summary>
    void Build();

    /// <summary>呈现一次进度(签名即 BootTimeline.Presenter;step/detail 可为 null = 不动文案)。</summary>
    void Present(float frac, string? step, string? detail, bool forceDraw);

    /// <summary>退休:置死亡标志(挡住条移除后仍可能触发的 postfix)+ 释放自身节点。</summary>
    void Retire();
}
#nullable restore

/// <summary>
/// 主题注册表与选择。选择持久化在 user://itsloading_theme.txt(mod 自持,
/// 不依赖 BaseLib 存储格式);缺失/未知值回退 classic。
/// 新增主题 = 在 Factories 加一行(BaseLib 设置里的循环按钮自动多一档)。
/// </summary>
internal static class ThemeRegistry
{
    private const string StoragePath = "user://itsloading_theme.txt";
    private const string DefaultId = "classic";

    private static readonly Dictionary<string, Func<ILoadingTheme>> Factories = new()
    {
        // 默认(也是 v1 唯一)主题:原底部细条,自 v0.5 起的外观原样搬迁
        ["classic"] = static () => new ClassicBar(),
    };

    /// <summary>主题 id 列表(稳定排序,循环切换的顺序)。</summary>
    internal static string[] Ids => Factories.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>当前选择的主题 id(读持久化文件;任何异常回退默认)。</summary>
    internal static string CurrentId()
    {
        try
        {
            string s = File.ReadAllText(ProjectSettings.GlobalizePath(StoragePath)).Trim();
            return Factories.ContainsKey(s) ? s : DefaultId;
        }
        catch
        {
            return DefaultId;
        }
    }

    /// <summary>构建当前选择的主题并建 UI(Init 的 build bar 步骤)。</summary>
    internal static ILoadingTheme BuildActive()
    {
        var theme = Factories[CurrentId()]();
        theme.Build();
        return theme;
    }

    /// <summary>
    /// BaseLib 设置入口回调:循环到下一个主题并持久化。
    /// 条在启动早期已建好、设置页只在菜单期可达 → 选择自下次启动生效。
    /// </summary>
    internal static string CycleNext()
    {
        string[] ids = Ids;
        string next = ids[(Array.IndexOf(ids, CurrentId()) + 1) % ids.Length];
        try
        {
            File.WriteAllText(ProjectSettings.GlobalizePath(StoragePath), next);
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to persist theme choice: {e}");
            return CurrentId();
        }
        Log.Warn($"[ItsLoading] theme '{next}' selected (applies from next launch)");
        return next;
    }
}
