using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 默认主题:经典底部条
//
// 设计约束:
//   1. 不用 Container/CenterContainer —— 其布局走 deferred 排序,同步突发期间不执行(内容挤在 0×0)
//   2. 全部节点手动定位
//   3. gd splash(0→0.25 段)与本条样式一致 —— frame 0 接管无视觉跳变

internal sealed class ClassicBar : ILoadingTheme
{
    // ---- 经典主题样式常量 ----
    // BootSplash 生成 gd 脚本时将这些颜色与几何常量全部插值进去；首启 C# 兜底
    // 与帧 0 gd 视图因此共用同一组值,无需人工维持两份布局。
    internal static readonly Color BarTrackColor = new(1f, 1f, 1f, 0.15f);        // 轨道
    internal static readonly Color BarDetailColor = new(0.62f, 0.64f, 0.70f, 1f); // 细节文字
    internal static readonly Color BarFillColor = new(0.2f, 0.85f, 0.9f, 1f);     // 填充
    internal static readonly Color OverallFillColor = new(0.2f, 0.85f, 0.9f, 0.55f);

    internal const float HorizontalPadding = 24f;
    internal const float StripHeight = 76f;
    internal const float StepY = 6f;
    internal const float DetailY = 31f;
    internal const float OverallY = 55f;
    internal const float OverallHeight = 3f;
    internal const float LocalY = 66f;
    internal const float LocalHeight = 5f;
    internal const float IndeterminateMinWidth = 60f;
    internal const float IndeterminateTravel = 160f;

    private CanvasLayer _layer;
    private Label _stepLabel;   // 第一行:当前步骤 + 计数
    private Label _detailLabel; // 第二行:当前对象 + 耗时
    private ColorRect _overallFill;
    private ColorRect _localFill;
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
        // 本条(999)叠加在 gd 条(998)上方渲染,透明设计会让两层文字交叠;
        // 同几何黑底一旦出帧即完全遮住 gd 条——交接时刻无论早晚都无缝。
        var strip = new Control();
        strip.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        strip.OffsetTop = -StripHeight;
        _layer.AddChild(strip);
        var backing = new ColorRect { Color = new Color(0f, 0f, 0f, 1f) };
        backing.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backing.OffsetTop = -2f;
        backing.OffsetBottom = 2f;
        strip.AddChild(backing);

        _stepLabel = new Label();
        _stepLabel.Position = new Vector2(HorizontalPadding, StepY);
        _stepLabel.AddThemeFontSizeOverride("font_size", 20);
        _stepLabel.AddThemeColorOverride("font_color", Colors.White);
        _stepLabel.Text = "";
        strip.AddChild(_stepLabel);

        _detailLabel = new Label();
        _detailLabel.Position = new Vector2(HorizontalPadding, DetailY);
        _detailLabel.AddThemeFontSizeOverride("font_size", 14);
        _detailLabel.AddThemeColorOverride("font_color", BarDetailColor);
        _detailLabel.Text = "";
        strip.AddChild(_detailLabel);

        _barFullWidth = vs.X - HorizontalPadding * 2f;
        _overallFill = AddBar(strip, OverallY, OverallHeight, OverallFillColor);
        _localFill = AddBar(strip, LocalY, LocalHeight, BarFillColor);

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

    private ColorRect AddBar(Control strip, float y, float height, Color fillColor)
    {
        var track = new ColorRect
        {
            Position = new Vector2(HorizontalPadding, y),
            Size = new Vector2(_barFullWidth, height),
            Color = BarTrackColor,
        };
        strip.AddChild(track);
        var fill = new ColorRect
        {
            Position = Vector2.Zero,
            Size = new Vector2(0f, height),
            Color = fillColor,
        };
        track.AddChild(fill);
        return fill;
    }

    private static string StageText(LoadingViewState state) => I18n.T("bar.stage", new()
    {
        ["n"] = ((int)state.Stage).ToString(),
        ["t"] = LoadingViewState.StageCount.ToString(),
        ["name"] = state.Step ?? "",
    });

    /// <summary>呈现全程条 + 当前阶段条 + 两行文案。</summary>
    public void Present(LoadingViewState state)
    {
        if (!UiOk || _barDead) return;
        ItsLoading.Run("update bar", () =>
        {
            _overallFill.Size = new Vector2(_barFullWidth * state.Overall, OverallHeight);
            _localFill.Size = new Vector2(
                state.LocalIndeterminate
                    ? Math.Min(IndeterminateMinWidth, _barFullWidth)
                    : _barFullWidth * state.Local,
                LocalHeight);
            if (state.Step != null) _stepLabel.Text = StageText(state);
            if (state.Detail != null) _detailLabel.Text = state.Detail;
            if (state.ForceDraw) RenderingServer.ForceDraw();
        });
    }

    public void Retire()
    {
        _barDead = true;
        _layer?.QueueFree();
    }
}
