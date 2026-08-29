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
    /// localization/<语言>/settings_ui.json 提供按语言标题);
    /// 根命名空间同时决定配置文件名 user://mod_configs/ItsLoading.cfg
    /// (ThemeRegistry.CfgPath 与之耦合,改名必须双侧同步)。
    /// 行/按钮标签走游戏 loc 表(键 ITSLOADING-<名>.title;下拉框枚举项键
    /// ITSLOADING-THEME.<枚举名>,缺键回退枚举原名)。
    /// </summary>
    public sealed class WaterfallConfig : SimpleModConfig
    {
        [ConfigButton("View")]
        public void OpenWaterfall() => global::ItsLoading.WaterfallViewer.CompatHooks.OpenWaterfall();

        /// <summary>
        /// 加载主题下拉框(BaseLib 把 enum 静态属性渲染成 NConfigDropdown)。
        /// 纯透传:主题值以 cfg 文件为准——getter 直读,setter 同步写。
        /// BaseLib 全生命周期对它幂等:Init 快照默认值 = 读文件;构造 Load 写回
        /// = 文件自己的值;Save 序列化 getter = 同值重写;恢复默认 → Classic。
        /// </summary>
        public static LoadingTheme Theme
        {
            get => global::ItsLoading.WaterfallViewer.CompatHooks.GetTheme();
            set => global::ItsLoading.WaterfallViewer.CompatHooks.SetTheme(value);
        }
    }
}
