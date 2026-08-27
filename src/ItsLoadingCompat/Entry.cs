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
    /// 行/按钮标签走游戏 loc 表(键 ITSLOADING-<方法名>.title / ITSLOADING-<按钮文本>.title,见 pck)。
    /// </summary>
    public sealed class WaterfallConfig : SimpleModConfig
    {
        [ConfigButton("View")]
        public void OpenWaterfall() => global::ItsLoading.WaterfallViewer.CompatHooks.OpenWaterfall();

        /// <summary>循环切换加载主题(只有一个主题时无害;选择自下次启动生效)。</summary>
        [ConfigButton("Switch")]
        public void NextTheme() => global::ItsLoading.WaterfallViewer.CompatHooks.NextTheme();
    }
}
