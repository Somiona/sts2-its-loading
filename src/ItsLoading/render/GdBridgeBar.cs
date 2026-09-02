using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- gd 启动视图的 C# host
//
// 帧 0 起就在场的 LoadingBarBoot bootstrap 节点独占全程加载 UI —— C# 不再
// 自建条,BootTimeline 的快照原样转发给它;主题视觉全部在 gd 侧(boot.gd
// 装载主题)。
//
// 获得宿主节点的两条路径,同一份 gd 代码:
//   1. autoload 节点(正常路径):帧 0 起在场,版本精确匹配且未关闭 → 直接 attach
//   2. 晚期托管(兜底路径):autoload 缺席(首次安装)/ 版本不匹配(过渡启动)/
//      已被自身安全网提前关闭(attach 到已隐藏的视图 = 全程无条)——
//      takeover 掉旧节点,从磁盘以 CACHE_MODE_IGNORE 实例化本次启动刚刷新的
//      boot.gd,attach 新实例。三种情况主题显示都不受影响。
//
// 协议(与 render/boot.gd 成对,破坏性变更双侧同步升版本):
//   csharp_attach() / csharp_present(overall, local, stage, step, detail) /
//   takeover() / show_hint(text) —— 桥接后调用
//   bridge_version(实例变量,Get 读取)—— 精确版本协商
//   _done(实例变量,Get 读取)—— 关闭标志,探测用
// 移除:本类拥有所宿节点 —— Retire 直接对它调 takeover()(淡出或立即隐藏由 gd 决定)。
internal sealed class GdBridgeBar : ILoadingTheme
{
    internal const int BridgeVersion = 11;

    private readonly Godot.Node _node;
    private readonly bool _lateHosted;
    private bool _dead;       // 移除后挡住仍可能触发的 postfix Present
    private int _presents;    // 移除时的摘要行用,确认全程由同一视图消费

    private GdBridgeBar(Godot.Node node, bool lateHosted)
    {
        _node = node;
        _lateHosted = lateHosted;
    }

    /// <summary>探测/晚期托管并构建(成功即已 Build/attach,失败返回 null = 本次启动没有加载 UI)。</summary>
    internal static GdBridgeBar TryBuild()
    {
        Godot.Node node = null;
        try
        {
            node = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(BootSplash.AutoloadNodeName);
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading] gd bridge probe failed ({e.Message})");
            return null;
        }

        // 正常路径:在场、版本精确匹配、未关闭
        if (node != null && VersionOk(node) && !IsRetired(node))
        {
            return Attach(node, lateHosted: false);
        }

        if (node != null)
        {
            ItsLoading.Run("takeover stale boot view", () => node.Call("takeover"));
        }

        Godot.Node fresh = InstantiateFresh();
        if (fresh != null && VersionOk(fresh) && !IsRetired(fresh))
        {
            return Attach(fresh, lateHosted: true);
        }

        Log.Warn("[ItsLoading] no usable gd boot view — no loading UI this boot " +
                 "(gd files missing or failed to load; see refresh/late-host logs above)");
        return null;
    }

    /// <summary>从磁盘实例化本次启动刚刷新的 bootstrap(CACHE_MODE_IGNORE:绕过旧实例已载入的资源缓存)。</summary>
    private static Godot.Node InstantiateFresh()
    {
        try
        {
            var script = ResourceLoader.Load<GDScript>(BootSplash.BootGdUserPath, "",
                ResourceLoader.CacheMode.Ignore);
            if (script == null)
            {
                Log.Warn("[ItsLoading] late host: GDScript load returned null " +
                         $"({BootSplash.BootGdUserPath})");
                return null;
            }
            var node = script.New().As<Node>();
            if (node == null)
            {
                Log.Warn("[ItsLoading] late host: script.New() did not yield a Node");
                return null;
            }
            // _ready 全流程:自检 → 读 cfg → 装主题 → 建 UI;随后 attach 停掉前奏轮询
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(node);
            Log.Warn("[ItsLoading] late host: fresh boot.gd instantiated from disk");
            return node;
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading] late host failed ({e.Message})");
            return null;
        }
    }

    /// <summary>调用形状必须精确匹配;新增方法/参数不升版,破坏性变更双侧同步升版。</summary>
    internal static bool VersionCompatible(int nodeVersion) => nodeVersion == BridgeVersion;

    private static bool VersionOk(Godot.Node node)
    {
        Variant v = node.Get("bridge_version");
        return v.VariantType == Variant.Type.Int
            && VersionCompatible(v.AsInt32())
            && node.HasMethod("csharp_attach")
            && node.HasMethod("csharp_present");
    }

    /// <summary>旧视图可能已被自身安全网提前关闭(隐藏但节点还在)——关闭了就不复用。</summary>
    private static bool IsRetired(Godot.Node node)
    {
        Variant done = node.Get("_done");
        return done.VariantType == Variant.Type.Bool && done.AsBool();
    }

    private static GdBridgeBar Attach(Godot.Node node, bool lateHosted)
    {
        var bridge = new GdBridgeBar(node, lateHosted);
        bridge.Build();
        return bridge;
    }

    /// <summary>接管:通知 gd 停前奏轮询(不隐藏不替换节点);首次注入时挂提示。</summary>
    public void Build()
    {
        _node.Call("csharp_attach");
        if (BootSplash.InjectedThisRun && _node.HasMethod("show_hint"))
        {
            _node.Call("show_hint", I18n.T("hint.injected"));
        }
        // 前奏活动行回放(可选方法,旧脚本缺席即跳过):工坊扫描期主循环不迭代的
        // 启动形态下轮询零观测,补齐活动日志;正常启动下与实时轮询共用去重,为 no-op。
        if (_node.HasMethod("replay_boot_log"))
        {
            ItsLoading.Run("replay boot log", () => _node.Call("replay_boot_log"));
        }
        Log.Warn(_lateHosted
            ? "[ItsLoading] late-hosted fresh boot view attached — theme-faithful UI for this boot"
            : "[ItsLoading] persistent gd boot view attached");
    }

    private int _lastLogCount = -1;
    private string _lastLogTail = "";

    /// <summary>
    /// 转发共用视图模型到 gd 节点(v11):StepText 已含阶段包装,日志发全量流
    /// (含前奏行)。日志变更检测(计数+尾行)避免逐帧 marshal 60 条字符串;
    /// 空数组 = 未变,gd 侧沿用上次。forceDraw 时在 C# 侧配对强制出帧。
    /// </summary>
    public void Present(LoadingViewState state, PresentedSnapshot snap)
    {
        if (_dead || !GodotObject.IsInstanceValid(_node)) return;
        _presents++;
        ItsLoading.Run("bridge present", () =>
        {
            var log = new Godot.Collections.Array<string>();
            bool changed = snap.Log.Count != _lastLogCount
                || (snap.Log.Count > 0 && snap.Log[^1] != _lastLogTail);
            if (changed)
            {
                foreach (var line in snap.Log) log.Add(line);
                _lastLogCount = snap.Log.Count;
                _lastLogTail = snap.Log.Count > 0 ? snap.Log[^1] : "";
            }
            _node.Call("csharp_present",
                state.Overall, state.Local, (int)state.Stage,
                snap.StepText, snap.DetailText, log);
            if (state.ForceDraw) RenderingServer.ForceDraw();
        });
    }

    /// <summary>
    /// 移除:置死亡标志 + 对所宿节点调 takeover(淡出/立即隐藏由 gd 按帧是否
    /// 流动决定),并打一行摘要 —— 版本/主题/宿主/presents/nan 压缩进一行,
    /// 终端用户拷贝 godot.log 即可远程诊断(帧数另见 gd 侧
    /// "splash dismissed at frame N")。
    /// </summary>
    public void Retire()
    {
        if (_dead) return; // 原生接管时已退过一次(onAttach),RetireBar 的二次调用幂等
        _dead = true;
        ItsLoading.Run("dismiss boot view", () => _node.Call("takeover"));
        string theme = _node.Get("_theme_id").AsString();
        int nan = _node.Get("_nan_count").AsInt32();
        Log.Warn($"[ItsLoading] boot view retired: " +
                 $"v{typeof(ItsLoading).Assembly.GetName().Version} theme={theme} " +
                 $"host={(_lateHosted ? "late-host" : "autoload")} presents={_presents} nan={nan}");
    }
}
