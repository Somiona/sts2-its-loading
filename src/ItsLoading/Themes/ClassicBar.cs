using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

// ---------------------------------------------------------------- 默认主题:经典底部条(架构拆分 #7 自 ItsLoading.cs 原样搬迁)
//
// 设计原则(吃过的亏,v0.2 起验证):
//   1. 不用 Container/CenterContainer —— 其布局走 deferred 排序,同步突发期间不执行(内容挤在 0×0)
//   2. 全部节点手动定位 —— 唯一可靠模式
//   3. gd splash(0→0.25 段)与本条样式一致 —— frame 0 接管无视觉跳变

internal sealed class ClassicBar : ILoadingTheme
{
    // ---- 条样式共享常量(todo#10:gd splash 模板与 C# 条的唯一真源)----
    // 颜色是最易漂移的样式类(几何漂移肉眼立见,颜色微漂正是一次漏网),
    // 因此颜色由这里的常量插值进 BootSplash.cs 的模板;几何常量仍两侧成对,
    // 改布局时需手动同步(见模板内注释)。
    internal static readonly Color BarTrackColor = new(1f, 1f, 1f, 0.15f);        // 轨道
    internal static readonly Color BarDetailColor = new(0.62f, 0.64f, 0.70f, 1f); // 细节文字
    internal static readonly Color BarFillColor = new(0.2f, 0.85f, 0.9f, 1f);     // 填充

    private CanvasLayer _layer;
    private Label _stepLabel;   // 第一行:当前步骤 + 计数
    private Label _detailLabel; // 第二行:当前对象 + 耗时
    private ColorRect _fill;    // 进度填充
    private float _barFullWidth;

    private bool UiOk => _layer != null;

    // 条移除后 AssetLoadingSession.Process 的 postfix 仍可能触发(游戏内房间加载等),
    // 用死亡标志防止对已释放节点的操作每帧抛异常
    private bool _barDead;

    public void Build()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 vs = tree.Root.GetVisibleRect().Size;

        _layer = new CanvasLayer { Layer = 999 };

        // 定位容器 + 不透明黑垫底(上下各溢出 2px 盖严):
        // v0.13.x 双条并存期间,gd 条(998,冻结在「52/52」)一直在本条(999)下面渲染,
        // 两条都是透明设计时文字会交叠(用户可见的"gd 残留")。C# 条一旦开始出帧,
        // 同几何黑底即完全遮住 gd 条——交接时刻无论早晚都无缝(2026-08-27)。
        var strip = new Control();
        strip.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        strip.OffsetTop = -64f;
        _layer.AddChild(strip);
        var backing = new ColorRect { Color = new Color(0f, 0f, 0f, 1f) };
        backing.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backing.OffsetTop = -2f;
        backing.OffsetBottom = 2f;
        strip.AddChild(backing);

        _stepLabel = new Label();
        _stepLabel.Position = new Vector2(24f, 8f);
        _stepLabel.AddThemeFontSizeOverride("font_size", 20);
        _stepLabel.AddThemeColorOverride("font_color", Colors.White);
        _stepLabel.Text = I18n.T("bar.mods", new() { ["n"] = "1", ["t"] = Math.Max(1, ModManager.Mods.Count).ToString() });
        strip.AddChild(_stepLabel);

        _detailLabel = new Label();
        _detailLabel.Position = new Vector2(24f, 36f);
        _detailLabel.AddThemeFontSizeOverride("font_size", 14);
        _detailLabel.AddThemeColorOverride("font_color", BarDetailColor);
        _detailLabel.Text = "";
        strip.AddChild(_detailLabel);

        _barFullWidth = vs.X - 48f;
        var track = new ColorRect();
        track.Position = new Vector2(24f, 56f);
        track.Size = new Vector2(_barFullWidth, 5f);
        track.Color = BarTrackColor;
        strip.AddChild(track);

        _fill = new ColorRect();
        _fill.Position = Vector2.Zero;
        _fill.Size = new Vector2(0f, 5f);
        _fill.Color = BarFillColor;
        track.AddChild(_fill);

        // 首次注入提示(左上角,不挡条)
        if (BootSplash.InjectedThisRun)
        {
            var hint = new Label();
            hint.Text = I18n.T("hint.injected");
            hint.Position = new Vector2(24f, 24f);
            hint.AddThemeFontSizeOverride("font_size", 14);
            hint.AddThemeColorOverride("font_color", BarFillColor);
            _layer.AddChild(hint);
            var t = tree.CreateTimer(8.0);
            t.Timeout += () => ItsLoading.Run("hide injection hint", () => { if (GodotObject.IsInstanceValid(hint)) hint.QueueFree(); });
        }

        // 必须直接 AddChild:同步突发期间 deferred 队列永远不会执行
        // 注意:这里不 ForceDraw —— 首帧绘制交给紧随其后的 first paint(3 次冗余提交),
        // 条内容在 Init 末尾由 BeginMods 呈现(Build 里的初始 label 覆盖 first paint 窗口)
        tree.Root.AddChild(_layer);
        Log.Warn($"[ItsLoading] bar attached (viewport {vs.X}x{vs.Y})");
    }

    /// <summary>呈现(= BootTimeline.Presenter 的目标):条宽 + 两行文案 + 强制出帧。</summary>
    public void Present(float frac, string step, string detail, bool forceDraw)
    {
        if (!UiOk || _barDead) return;
        ItsLoading.Run("update bar", () =>
        {
            _fill.Size = new Vector2(_barFullWidth * Math.Clamp(frac, 0f, 1f), 5f);
            if (step != null) _stepLabel.Text = step;
            if (detail != null) _detailLabel.Text = detail;
            if (forceDraw) RenderingServer.ForceDraw();
        });
    }

    public void Retire()
    {
        _barDead = true;
        _layer?.QueueFree();
    }
}
