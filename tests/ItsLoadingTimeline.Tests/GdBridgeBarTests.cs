using ItsLoading;
using Xunit;

// gd 桥的契约回归(纯函数部分;节点交互不在单测范围):
//   版本门槛 —— 调用形状精确匹配 v9(BarFill 变换驱动:矩形终身满轨,
//   进度只写 scale/position)

public sealed class GdBridgeBarTests
{
    [Theory]
    [InlineData(0, false)]    // 无 bridge_version 字段的旧脚本:C# Get 到 Nil,不会进这里;显式 0 也不接管
    [InlineData(-1, false)]
    [InlineData(2, false)]    // 历史版本一律拒绝
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]     // 当前协议版本
    [InlineData(10, false)]   // 更新的版本必须显式适配后才能接管
    public void VersionCompatible_requires_exact_bridge_version(int nodeVersion, bool expected)
    {
        Assert.Equal(expected, GdBridgeBar.VersionCompatible(nodeVersion));
        Assert.Equal(9, GdBridgeBar.BridgeVersion);
    }
}
