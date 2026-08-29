using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 持久 gd 启动视图适配器
//
// 帧 0 起就在场的 LoadingBarBoot gd 节点独占全程加载 UI——
// C# 不再造 ClassicBar,BootTimeline 的快照原样转发给该节点。
//
// 协议(与 BootSplash.cs 模板内 gd 侧成对,破坏性变更必须双侧同步升版本):
//   csharp_attach()                    —— 接管确认:停工坊轮询/shimmer/30s 安全网,
//                                         节点保持可见、不被隐藏或替换
//   csharp_present(overall, local, stage,
//                  step, detail)        —— 双条完整快照
//   bridge_version(实例变量,Get 读取) —— 精确版本协商;不匹配则回退 ClassicBar
// 退休:本类不拥有节点——按 ILoadingTheme 契约,隐藏仍归 BootSplash.Takeover()。
//
internal sealed class GdBridgeBar : ILoadingTheme
{
    internal const int BridgeVersion = 2;

    private readonly Godot.Node _node;
    private bool _dead;       // 退休后挡住仍可能触发的 postfix Present(同 ClassicBar 死亡标志)
    private int _presents;    // 退休时汇总，便于确认全程由同一视图消费

    private GdBridgeBar(Godot.Node node) => _node = node;

    /// <summary>
    /// 探测兼容性并构建(成功即已 Build/attach,失败返回 null 回退 ClassicBar)。
    /// 失败三态各有诊断:节点不在场(首装/override.cfg 未采用)、在场但脚本旧
    /// (本次运行内存里是旧版,Install 已排队重写、下次启动生效)、缺桥方法。
    /// </summary>
    internal static GdBridgeBar TryBuild()
    {
        Godot.Node node = null;
        try
        {
            node = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(BootSplash.AutoloadNodeName);
        }
        catch (Exception e)
        {
            Log.Warn($"[ItsLoading] gd bridge probe failed ({e.Message}) — ClassicBar fallback");
            return null;
        }
        if (node == null)
        {
            Log.Warn("[ItsLoading] no compatible gd boot view (first run after install, " +
                     "or override.cfg not applied) — ClassicBar fallback");
            return null;
        }

        Variant v = node.Get("bridge_version");
        if (v.VariantType != Variant.Type.Int || !VersionCompatible(v.AsInt32()))
        {
            string found = v.VariantType == Variant.Type.Int ? v.AsInt32().ToString() : "missing";
            Log.Warn($"[ItsLoading] gd boot view version mismatch " +
                     $"(bridge_version={found}) — " +
                     $"gd rewrite queued this run, bridge active from next launch; ClassicBar fallback now");
            return null;
        }
        if (!node.HasMethod("csharp_attach") || !node.HasMethod("csharp_present"))
        {
            Log.Warn("[ItsLoading] gd boot view missing bridge methods — ClassicBar fallback");
            return null;
        }

        var bridge = new GdBridgeBar(node);
        bridge.Build();
        return bridge;
    }

    /// <summary>调用形状必须精确匹配；加法式协议保持同版，破坏性变更双侧升版。</summary>
    internal static bool VersionCompatible(int nodeVersion) => nodeVersion == BridgeVersion;

    /// <summary>接管:通知 gd 停轮询/shimmer(不隐藏不替换节点)。无自建 UI —— 帧 0 起它就在场。</summary>
    public void Build()
    {
        _node.Call("csharp_attach");
        Log.Warn("[ItsLoading] persistent gd boot view attached — ClassicBar not constructed");
    }

    /// <summary>转发 BootTimeline.Present 到 gd 节点;forceDraw 时在 C# 侧配对强制出帧(同 ClassicBar)。</summary>
    public void Present(LoadingViewState state)
    {
        if (_dead || !GodotObject.IsInstanceValid(_node)) return;
        _presents++;
        ItsLoading.Run("bridge present", () =>
        {
            _node.Call("csharp_present",
                state.Overall, state.Local, (int)state.Stage,
                state.Step ?? "", state.Detail ?? "");
            if (state.ForceDraw) RenderingServer.ForceDraw();
        });
    }

    /// <summary>退休:置死亡标志即可——节点隐藏归 BootSplash.Takeover()(ILoadingTheme 契约)。</summary>
    public void Retire()
    {
        _dead = true;
        Log.Warn($"[ItsLoading] gd boot view retired after {_presents} presents");
    }
}
