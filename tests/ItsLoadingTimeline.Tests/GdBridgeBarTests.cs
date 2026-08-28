using ItsLoading;
using Xunit;

// gd 桥的线上契约回归(纯函数部分;节点交互已有真机验证):
//   版本门槛 —— 调用形状精确匹配 v2

public sealed class GdBridgeBarTests
{
    [Theory]
    [InlineData(0, false)]   // 旧脚本:无 bridge_version 字段 → C# Get 到 Nil,不会进这里;显式 0 也不接管
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]    // 双条协议
    [InlineData(3, false)]   // 破坏性未来版本必须显式适配
    public void VersionCompatible_requires_exact_bridge_version(int nodeVersion, bool expected)
    {
        Assert.Equal(expected, GdBridgeBar.VersionCompatible(nodeVersion));
        Assert.Equal(2, GdBridgeBar.BridgeVersion);
    }
}
