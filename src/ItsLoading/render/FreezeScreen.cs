using System;

#nullable enable

namespace ItsLoading;

/// <summary>
/// 原生加载屏协调器(平台无关):决定“何时建 / 何时熔断”,像素全部交给
/// IThemeSurface。视图模型(文案/日志/时钟)在 LoadingPresentation,由
/// ItsLoading 的 Presenter 组合点统一供给双渲染器 —— 本类只做门控。门控语义(2026-09-01 实证校准,见 M14_METAL_FREEZE_ADDENDUM.md):
///   · 暖场:前 WarmupPresents 次呈现尚健康(mod#1 前 acquire 正常),不叠加双条
///   · 冻结确立:frames_drawn 停在首见值且已过暖场 → attach(并回调 onAttach,
///     ItsLoading 用它让 gd 主题退场 —— 原生面自此刻起拥有全部像素)
///   · 帧恢复 = 活跃模式:不再拆除,继续呈现直到 RetireBar —— beta 上 Godot 侧
///     呈现在启动突发期全部不上屏(合成器留存 boot logo 帧),原生面是唯一
///     可见通道;stable/Windows 上冻结永不确立,本类全程闲置,gd 照常主渲染
///   · 熔断:活跃期异常 → 拆面 + 回调 onBroken(ItsLoading 复活 gd 晚期托管)
///   · 开关:ITSLOADING_NO_MAC_OVERLAY=1 环境变量;总开关是设置里的
///     (Beta)原生加载屏渲染器(ItsLoading.Init 不创建本类 = 连细条也没有)
/// 依赖全部经构造注入(帧计数/主线程判定/环境变量),纯逻辑可离线单测。
/// </summary>
internal sealed class FreezeScreen
{
    private const string EnvOff = "ITSLOADING_NO_MAC_OVERLAY";

    /// <summary>冻结判定的启动门限:前几次呈现尚健康,不应叠加双条。</summary>
    private const int WarmupPresents = 3;

    private readonly Func<long> _framesDrawn;
    private readonly Func<bool> _onMainThread;
    private readonly Func<string, string?> _env;
    private readonly Func<IThemeSurface?> _surfaceFactory;
    private readonly Action<string>? _warn;
    private readonly Action? _onAttach;
    private readonly Action? _onBroken;

    private bool _off, _broken, _dead, _attached, _alive;
    private long _firstFrame = long.MinValue;
    private int _presents;
    private IThemeSurface? _surface;

    internal FreezeScreen(Func<long> framesDrawn, Func<bool> onMainThread,
        Func<string, string?> env, Func<IThemeSurface?> surfaceFactory,
        Action<string>? warn = null, Action? onAttach = null, Action? onBroken = null)
    {
        _framesDrawn = framesDrawn;
        _onMainThread = onMainThread;
        _env = env;
        _surfaceFactory = surfaceFactory;
        _warn = warn;
        _onAttach = onAttach;
        _onBroken = onBroken;
    }

    /// <summary>Present 组合点调用(ItsLoading 的 Presenter 先经 LoadingPresentation)。</summary>
    internal void Present(LoadingViewState state, PresentedSnapshot snap)
    {
        if (_off || _broken || _dead) return;
        // 必须先验主线程再碰任何引擎/原生调用
        if (!_onMainThread()) return;
        try
        {
            long f = _framesDrawn();
            if (_firstFrame == long.MinValue) _firstFrame = f;
            _presents++;
            if (f != _firstFrame)
            {
                // 帧恢复:已挂载 → 活跃模式(继续呈现);未挂载 = 健康启动,闲置
                if (!_attached) return;
                _alive = true;
            }
            else
            {
                if (_presents <= WarmupPresents) return; // 健康期不叠加
                if (!_attached && !TryAttach()) return;
            }
            _surface!.Present(new SurfaceView(state, snap));
        }
        catch (Exception e)
        {
            _broken = true;
            try { _surface?.Remove("exception: " + e.Message); _surface?.Teardown(); }
            catch { /* 双熔断 */ }
            _warn?.Invoke($"[ItsLoading][overlay] disabled: {e.Message}");
            _onBroken?.Invoke();
        }
    }

    /// <summary>RetireBar:开始退场(淡出);真正的硬拆由 Teardown 完成。</summary>
    internal void Remove(string why)
    {
        if (_dead) return;
        _dead = true;
        if (!_attached) return;
        if (!_onMainThread()) return;
        try
        {
            _surface!.Remove(why);
            _warn?.Invoke($"[ItsLoading][overlay] removed ({why}) frame={_framesDrawn()}");
        }
        catch (Exception e)
        {
            _warn?.Invoke($"[ItsLoading][overlay] remove failed ({why}): {e.Message}");
        }
    }

    /// <summary>淡出步进与硬拆(RetireBar 的两段式退场;主线程)。</summary>
    internal void SetOpacity(double opacity)
    {
        if (!_attached || _broken) return;
        if (!_onMainThread()) return;
        try { _surface!.SetOpacity(opacity); }
        catch (Exception e) { _warn?.Invoke($"[ItsLoading][overlay] fade failed: {e.Message}"); }
    }

    internal void Teardown()
    {
        if (!_attached) return;
        if (!_onMainThread()) return;
        try { _surface!.Teardown(); _attached = false; }
        catch (Exception e) { _warn?.Invoke($"[ItsLoading][overlay] teardown failed: {e.Message}"); }
    }

    /// <summary>挂载后是否进入过活跃模式(帧已恢复;诊断/测试用)。</summary>
    internal bool IsAlive => _alive;

    private bool TryAttach()
    {
        _off = _env(EnvOff) == "1";
        if (_off) return false;
        IThemeSurface? surface = _surfaceFactory();
        if (surface == null)
        {
            // 平台无适配面(如 Windows):与旧 !IsMacOS 分支同款,静默闲置
            _dead = true;
            return false;
        }
        if (!surface.TryAttach())
        {
            _broken = true; // 呈现面已自行打日志(层找不到/主题不可用且细条兜底也失败)
            return false;
        }
        _surface = surface;
        _attached = true;
        _warn?.Invoke("[ItsLoading][overlay] attached — native surface owns the pixels now");
        _onAttach?.Invoke();
        return true;
    }

}
