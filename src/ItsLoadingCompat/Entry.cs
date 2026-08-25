using BaseLib.Config;

namespace ItsLoadingCompat
{
    /// <summary>
    /// BaseLib 兼容垫片:仅由主 dll 在确认 BaseLib 已加载后 Assembly.LoadFrom 装载。
    /// </summary>
    public static class Entry
    {
        public static void Register(string modId)
        {
            ModConfigRegistry.Register(modId, new ItsLoading.WaterfallConfig());
        }
    }
}

namespace ItsLoading
{
    /// <summary>
    /// 注意:BaseLib 的 ModPrefix 取自本类型的根命名空间(大写 + "-"),
    /// 命名空间必须是 ItsLoading → 本地化键 ITSLOADING-mod_title(pck 内
    /// localization/<语言>/settings_ui.json 提供按语言标题)。
    /// 行标签 = 方法名(本地化缺失原文回退)。
    /// </summary>
    public sealed class WaterfallConfig : SimpleModConfig
    {
        [ConfigButton("查看")]
        public void 启动瀑布图() => global::ItsLoading.ItsLoading.CompatHooks.OpenWaterfall();
    }
}
