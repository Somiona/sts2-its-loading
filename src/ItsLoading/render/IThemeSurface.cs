namespace ItsLoading;

/// <summary>
/// 冻结期呈现面端口:窗口内原生像素的平台适配(macOS = CALayer 子树,
/// Windows = 独立 layered window;工厂返回 null = 平台无适配面,静默闲置)。
/// 生命周期归 SurfaceRouter(何时建/何时熔断),呈现面只负责像素:
///   TryAttach —— 定位窗口/层、量边界、建原生视图树(失败 = 本次启动不用)
///   Present   —— 应用一次快照(冻结期经显式事务直达系统呈现服务器;
///                帧恢复后继续 —— 原生面自挂载起拥有像素直到 retire)
///   SetOpacity / Teardown —— SurfaceRouter 的统一退场策略所需平台原语
/// 约束:所有动词都只在主线程被调(SurfaceRouter 保证)。
/// </summary>
internal interface IThemeSurface
{
    bool TryAttach();
    void Present(LoadingFrame frame);
    void Teardown();
    void SetOpacity(double opacity);
}
