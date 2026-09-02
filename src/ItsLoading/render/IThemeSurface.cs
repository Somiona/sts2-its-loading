namespace ItsLoading;

/// <summary>呈现面的单次快照:原始状态 + LoadingPresentation 的共用视图模型
///(StepText 已含阶段包装、Log 含前奏历史、T 为共用不定相位时钟)。</summary>
internal readonly record struct SurfaceView(LoadingViewState State, PresentedSnapshot Snap);

/// <summary>
/// 冻结期呈现面端口:窗口内原生像素的平台适配(macOS = CALayer 子树,
/// Windows = 未开工;工厂返回 null = 平台无适配面,静默闲置)。
/// 生命周期归 FreezeScreen(何时建/何时熔断),呈现面只负责像素:
///   TryAttach —— 定位窗口/层、量边界、建原生视图树(失败 = 本次启动不用)
///   Present   —— 应用一次快照(冻结期经显式事务直达系统呈现服务器;
///                帧恢复后继续 —— 原生面自挂载起拥有像素直到 retire)
///   Remove    —— 退场(淡出开始;Teardown 才真正拆层)
///   Teardown  —— 硬拆干净(淡出结束后由调用方触发)
///   SetOpacity —— 淡出步进(0..1)
/// 约束:所有动词都只在主线程被调(FreezeScreen 保证)。
/// </summary>
internal interface IThemeSurface
{
    bool TryAttach();
    void Present(SurfaceView view);
    void Remove(string why);
    void Teardown();
    void SetOpacity(double opacity);
}
