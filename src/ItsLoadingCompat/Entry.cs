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

        [ConfigButton("OpenGallery")]
        public void OpenThemeGallery() => global::ItsLoading.WaterfallViewer.CompatHooks.OpenGallery();

        /// <summary>
        /// (Beta)原生加载屏渲染器(BaseLib 把 bool 静态属性渲染成开关)。
        /// 默认开;关 = 仅旧 gd+C# 路径,无任何原生冻结呈现,下次启动生效。
        /// 注意:BaseLib「恢复默认」会把属性重置为 C# 默认 false(= 关),
        /// 与我们"缺省=开"的文件语义不同 —— 可接受的边缘行为。
        /// </summary>
        public static bool NativeRenderer
        {
            get => global::ItsLoading.WaterfallViewer.CompatHooks.GetNativeRenderer();
            set => global::ItsLoading.WaterfallViewer.CompatHooks.SetNativeRenderer(value);
        }

        /// <summary>
        /// (Debug)开发者标定视图(默认关):双渲染器同规则的品红元素框 + 10% 网格,
        /// 主题开发/布局比对用。gd 与原生呈现面同时生效,下次启动起。
        /// </summary>
        public static bool CalibView
        {
            get => global::ItsLoading.WaterfallViewer.CompatHooks.GetCalibView();
            set => global::ItsLoading.WaterfallViewer.CompatHooks.SetCalibView(value);
        }
    }
}
