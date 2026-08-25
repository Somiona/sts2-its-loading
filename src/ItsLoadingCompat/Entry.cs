using BaseLib.Config;

namespace ItsLoadingCompat;

/// <summary>
/// BaseLib 兼容垫片:仅由主 dll 在确认 BaseLib 已加载后 Assembly.LoadFrom 装载。
/// 行标签 = 方法名(BaseLib 本地化缺失时原文回退,故用中文方法名)。
/// </summary>
public static class Entry
{
    public static void Register(string modId)
    {
        ModConfigRegistry.Register(modId, new WaterfallConfig());
    }
}

public sealed class WaterfallConfig : SimpleModConfig
{
    [ConfigButton("查看")]
    public void 启动瀑布图() => global::ItsLoading.ItsLoading.CompatHooks.OpenWaterfall();
}
