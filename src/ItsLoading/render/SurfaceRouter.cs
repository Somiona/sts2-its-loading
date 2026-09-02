using System;
using System.Threading.Tasks;

#nullable enable

namespace ItsLoading;

/// <summary>
/// 两条呈现路径的唯一所有者。Godot 是基础路径；native factory 非空表示用户允许
/// 在冻结时尝试接管。上游只推 LoadingFrame，renderer 之间互不引用，也不能要求
/// Timeline replay。Router 保存最新帧，native 熔断后直接恢复仍挂载的 Godot 面。
/// </summary>
internal sealed class SurfaceRouter
{
    private const string EnvOff = "ITSLOADING_NO_MAC_OVERLAY";
    private const int WarmupPresents = 3;

    private readonly IGodotSurface? _godot;
    private readonly Func<long> _framesDrawn;
    private readonly Func<bool> _onMainThread;
    private readonly Func<IThemeSurface?>? _nativeFactory;
    private readonly Action<string>? _warn;
    private readonly Func<int, Task> _delay;

    private IThemeSurface? _native;
    private LoadingFrame _latest;
    private long _lastObservedFrame = long.MinValue;
    private int _stablePresents;
    private bool _nativeDead;
    private bool _retired;
    private Task? _fadeTask;
    private Task? _retireTask;

    internal SurfaceRouter(IGodotSurface? godot, Func<long> framesDrawn,
        Func<bool> onMainThread, Func<string, string?> env,
        Func<IThemeSurface?>? nativeFactory, Action<string>? warn = null,
        Func<int, Task>? delay = null)
    {
        _godot = godot;
        _framesDrawn = framesDrawn;
        _onMainThread = onMainThread;
        _nativeFactory = nativeFactory;
        _warn = warn;
        _delay = delay ?? Task.Delay;
        _nativeDead = nativeFactory == null || env(EnvOff) == "1";
    }

    internal bool NativeActive => _native != null && !_nativeDead;

    internal void Present(LoadingFrame frame)
    {
        if (_retired || !_onMainThread()) return;
        _latest = frame;

        if (NativeActive)
        {
            try { _native!.Present(frame); }
            catch (Exception e) { FallBackToGodot(e); }
            if (frame.Stage == BootStage.Menu) _ = BeginFade("final-stage");
            return;
        }

        _godot?.Present(frame);
        if (frame.Stage == BootStage.Menu)
        {
            _ = BeginFade("final-stage");
            return;
        }
        TryNativeTakeover();
    }

    private void TryNativeTakeover()
    {
        if (_nativeDead) return;
        long frame = _framesDrawn();
        if (frame == _lastObservedFrame) _stablePresents++;
        else
        {
            _lastObservedFrame = frame;
            _stablePresents = 1;
        }
        if (_stablePresents <= WarmupPresents) return;

        IThemeSurface? candidate = _nativeFactory!();
        if (candidate == null)
        {
            _nativeDead = true;
            return;
        }
        try
        {
            if (!candidate.TryAttach())
            {
                candidate.Teardown();
                _nativeDead = true;
                return;
            }
            // 先完成首帧，再切换 route；失败时 Godot 从未失去所有权。
            candidate.Present(_latest);
            _godot?.SetVisible(false);
            _native = candidate;
            _warn?.Invoke("[ItsLoading][overlay] attached — native surface owns the pixels now");
        }
        catch (Exception e)
        {
            try { candidate.Teardown(); } catch { }
            try { _godot?.SetVisible(true); } catch { }
            _nativeDead = true;
            _warn?.Invoke($"[ItsLoading][overlay] attach failed: {e.Message}");
        }
    }

    private void FallBackToGodot(Exception error)
    {
        _nativeDead = true;
        try { _native?.Teardown(); }
        catch { }
        _native = null;
        _warn?.Invoke($"[ItsLoading][overlay] disabled: {error.Message} — Godot resumed");
        _godot?.SetVisible(true);
        _godot?.Present(_latest);
    }

    /// <summary>视觉退场与资源销毁分离：Stage 7 只淡出，安全点才调用 Retire。</summary>
    private Task BeginFade(string why) => _fadeTask ??= FadeCore(why);

    private async Task FadeCore(string why)
    {
        _retired = true;
        _godot?.Retire();
        if (_native == null) return;
        try
        {
            for (int i = 7; i >= 1; i--)
            {
                _native.SetOpacity(i / 8.0);
                await _delay(50);
            }
        }
        catch (Exception e)
        {
            _warn?.Invoke($"[ItsLoading][overlay] fade failed ({why}): {e.Message}");
        }
        _warn?.Invoke("[ItsLoading][overlay] fade complete — resources retained until safe point");
    }

    internal Task Retire(string why) => _retireTask ??= RetireCore(why);

    private async Task RetireCore(string why)
    {
        await BeginFade(why);
        if (_native == null) return;
        try
        {
            _native.Teardown();
            _warn?.Invoke("[ItsLoading][overlay] disposed at safe point");
        }
        catch (Exception e) { _warn?.Invoke($"[ItsLoading][overlay] teardown failed: {e.Message}"); }
        _native = null;
    }
}
