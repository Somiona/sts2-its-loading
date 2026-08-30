using System.IO;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 启动画面(gd splash)自注入
//
// 帧 0 起效的 GDScript 经典双条:先自行覆盖工坊阶段,随后经 GdBridgeBar
// 接收 C# 时间线快照并持续到菜单就绪。安装、协议模板、锚点与延迟回收集中在本类。

internal static class BootSplash
{
    private const string AutoloadName = "LoadingBarBoot";
    private const string GdUserPath = "user://loadingbar_boot.gd";
    private const string CfgMarker = "; LoadingBar mod autoload";

    /// <summary>gd splash 自动载入节点名(GdBridgeBar 探测/Handoff 寻址共用)。</summary>
    internal static string AutoloadNodeName => AutoloadName;

    private static bool _injectedThisRun;
    private static Godot.Node _bootSplashNode; // 延迟清理的 gd splash 引用

    /// <summary>本次运行是否刚完成注入(BuildBar 据此显示首次注入提示)。</summary>
    internal static bool InjectedThisRun => _injectedThisRun;

    internal static void Install()
    {
        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
        string cfgPath = Path.Combine(exeDir, "override.cfg");
        string gdPath = ProjectSettings.GlobalizePath(GdUserPath);

        bool cfgOk = false, gdOk = false;
        try { cfgOk = File.Exists(cfgPath) && File.ReadAllText(cfgPath).Contains(AutoloadName); } catch { }
        try
        {
            // 哈希门控:子串匹配有洞(损坏的文件照样"包含"版本串,不会触发重写)
            gdOk = File.Exists(gdPath)
                && System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(gdPath))
                    .SequenceEqual(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(BootSplashGd)));
        }
        catch { }

        if (!gdOk)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(gdPath)!);
            File.WriteAllText(gdPath, BootSplashGd, new UTF8Encoding(false));
            Log.Warn("[ItsLoading] wrote " + gdPath);
        }
        if (!cfgOk)
        {
            string body = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
            var sb = new StringBuilder(body);
            if (body.Length > 0 && !body.EndsWith("\n")) sb.Append('\n');
            sb.Append('\n').Append(CfgMarker).Append('\n')
              .Append("[autoload]\n\n")
              .Append(AutoloadName).Append("=\"*").Append(GdUserPath).Append("\"\n");
            File.WriteAllText(cfgPath, sb.ToString());
            Log.Warn("[ItsLoading] wrote " + cfgPath);
        }
        if (cfgOk && gdOk) Log.Warn("[ItsLoading] boot splash already installed");
        _injectedThisRun = !cfgOk || !gdOk;
    }

    internal static void Handoff()
    {
        var boot = ((SceneTree)Engine.GetMainLoop()).Root.GetNodeOrNull(AutoloadName);
        if (boot != null)
        {
            // 引擎启动锚点(gd frame 0)+ 前奏阶段 spans:写入启动时间线(单一写缝)
            Variant anchor = boot.Get("boot_start_msec");
            if (anchor.VariantType == Variant.Type.Int)
            {
                ItsLoading.Timeline?.SetBootAnchor(anchor.AsInt64());
            }
            // 工坊扫描时序(gd 轮询观测):转成逐项 Prelude span。加法式桥扩展,
            // 旧脚本(无 get_workshop_log)优雅跳过。
            if (boot.HasMethod("get_workshop_log"))
            {
                ItsLoading.Run("read workshop timing", () =>
                {
                    Variant wl = boot.Call("get_workshop_log");
                    if (wl.VariantType != Variant.Type.Array) return;
                    var arr = wl.AsGodotArray();
                    if (arr.Count < 2) return;
                    double endMs = arr[0].AsDouble();
                    var names = arr.Count > 2 && arr[2].VariantType == Variant.Type.Dictionary
                        ? arr[2].AsGodotDictionary() : null;
                    var entries = new System.Collections.Generic.List<(string, string, double)>();
                    if (arr[1].VariantType == Variant.Type.Array)
                    {
                        foreach (Variant e in arr[1].AsGodotArray())
                        {
                            var pair = e.AsGodotArray();
                            if (pair.Count < 2) continue;
                            string id = pair[0].AsString();
                            entries.Add((id,
                                names != null && names.ContainsKey(id) ? names[id].AsString() : "",
                                pair[1].AsDouble()));
                        }
                    }
                    if (entries.Count > 0)
                    {
                        ItsLoading.Timeline?.RecordWorkshopScan(entries, endMs);
                        Log.Warn($"[ItsLoading] workshop timing imported ({entries.Count} items)");
                    }
                });
            }
            // 正常路径:该节点已经由 GdBridgeBar 接管,继续作为唯一 UI。
            // 首启/版本不匹配:ClassicBar(999)覆盖本节点(998),仍不在同步突发期隐藏它，
            // 避免 MoltenVK 因渲染状态变化出现黑屏间隙。
            _bootSplashNode = boot;
            Log.Warn(ItsLoading.Theme is GdBridgeBar
                ? "[ItsLoading] gd boot view retained as the active loading UI"
                : "[ItsLoading] old gd boot view kept under ClassicBar fallback");
        }
        else
        {
            // 三种可能:真·首装(本 run 刚注入,下次启动生效)/ override.cfg 未被引擎采用 /
            // gd 脚本加载失败。日志里若同时没有任何 [LoadingBarBoot] 行,可定位到后两者。
            Log.Warn("[ItsLoading] no boot splash node — first run after install, " +
                     "override.cfg not applied, or boot script failed to load");
        }
    }

    /// <summary>C# 条移除时才隐藏 gd splash(延迟清理,由 BeforeDeferred 的移除定时器调用)。</summary>
    internal static void Takeover() => _bootSplashNode?.Call("takeover");

    /// <summary>
    /// GDScript 启动画面源码(生成产物,内容哈希门控自注入)。
    /// 替换 token(由 BuildBootSplashGd() 用共享常量替换,勿在模板里写死):
    ///   @@MOD_ID@@ / @@AUTOLOAD_NAME@@ / @@GD_USER_PATH@@ / @@CFG_MARKER@@ —— 身份契约,
    ///   与 C# 侧常量同源(漂移 = 自清理失灵,故必须走 token)
    ///   @@*_COLOR@@ / @@*_Y@@ / @@*_HEIGHT@@ 等 —— ClassicBar 的样式常量,
    ///   gd 正常路径与 C# 首启兜底共用同一组常量。
    ///   @@THEME_CFG_PATH@@ / @@THEME_LEGACY_PATH@@ / @@MOD_VERSION@@ / @@MS_*@@ / @@SS_*@@ ——
    ///   主题配置(BaseLib cfg 为主 + 迁移期旧 txt 回退)、版本串与
    ///   MinespireBar / SlaytheshinBar 布局常量(模板按主题值分支三套布局,与 ThemeRegistry 一致)。
    /// </summary>
    private static readonly string BootSplashGd = BuildBootSplashGd();

    private static string BuildBootSplashGd() =>
        BootSplashGdTemplate
            .Replace("@@MOD_ID@@", ItsLoading.ModId)
            .Replace("@@AUTOLOAD_NAME@@", AutoloadName)
            .Replace("@@GD_USER_PATH@@", GdUserPath)
            .Replace("@@CFG_MARKER@@", CfgMarker)
            .Replace("@@BRIDGE_VERSION@@", GdBridgeBar.BridgeVersion.ToString())
            .Replace("@@STAGE_COUNT@@", LoadingViewState.StageCount.ToString())
            .Replace("@@WORKSHOP_END@@", GdFloat(BootTimeline.WorkshopEnd))
            .Replace("@@TRACK_COLOR@@", GdColor(ClassicBar.BarTrackColor))
            .Replace("@@DETAIL_COLOR@@", GdColor(ClassicBar.BarDetailColor))
            .Replace("@@FILL_COLOR@@", GdColor(ClassicBar.BarFillColor))
            .Replace("@@OVERALL_FILL_COLOR@@", GdColor(ClassicBar.OverallFillColor))
            .Replace("@@PAD@@", GdFloat(ClassicBar.HorizontalPadding))
            .Replace("@@STRIP_HEIGHT@@", GdFloat(ClassicBar.StripHeight))
            .Replace("@@STEP_Y@@", GdFloat(ClassicBar.StepY))
            .Replace("@@DETAIL_Y@@", GdFloat(ClassicBar.DetailY))
            .Replace("@@OVERALL_Y@@", GdFloat(ClassicBar.OverallY))
            .Replace("@@OVERALL_HEIGHT@@", GdFloat(ClassicBar.OverallHeight))
            .Replace("@@LOCAL_Y@@", GdFloat(ClassicBar.LocalY))
            .Replace("@@LOCAL_HEIGHT@@", GdFloat(ClassicBar.LocalHeight))
            .Replace("@@PULSE_MIN@@", GdFloat(ClassicBar.IndeterminateMinWidth))
            .Replace("@@PULSE_TRAVEL@@", GdFloat(ClassicBar.IndeterminateTravel))
            // Minespire 主题:布局常量在 MinespireBar,模板不写死(同 ClassicBar)
            .Replace("@@THEME_CFG_PATH@@", ThemeRegistry.CfgPath)
            .Replace("@@THEME_LEGACY_PATH@@", ThemeRegistry.LegacyTxtPath)
            .Replace("@@MOD_VERSION@@", typeof(ItsLoading).Assembly.GetName().Version?.ToString() ?? "")
            .Replace("@@MS_BG_COLOR@@", GdColor(MinespireBar.BgColor))
            .Replace("@@MS_TEXT_COLOR@@", GdColor(MinespireBar.TextColor))
            .Replace("@@MS_DIM_COLOR@@", GdColor(MinespireBar.DimTextColor))
            .Replace("@@MS_DESIGN_W@@", GdFloat(MinespireBar.DesignW))
            .Replace("@@MS_DESIGN_H@@", GdFloat(MinespireBar.DesignH))
            .Replace("@@MS_BAR_W@@", GdFloat(MinespireBar.BarWidth))
            .Replace("@@MS_BAR_H@@", GdFloat(MinespireBar.BarHeight))
            .Replace("@@MS_BARS_TOP@@", GdFloat(MinespireBar.BarsTop))
            .Replace("@@MS_LABEL_GAP@@", GdFloat(MinespireBar.LabelGap))
            .Replace("@@MS_BAR_GAP@@", GdFloat(MinespireBar.BarGap))
            .Replace("@@MS_STEP_LABEL_H@@", GdFloat(MinespireBar.StepLabelH))
            .Replace("@@MS_DETAIL_LABEL_H@@", GdFloat(MinespireBar.DetailLabelH))
            .Replace("@@MS_BORDER_W@@", GdFloat(MinespireBar.BorderWidth))
            .Replace("@@MS_FILL_INSET@@", GdFloat(MinespireBar.FillInset))
            .Replace("@@MS_LOGO_Y@@", GdFloat(MinespireBar.LogoY))
            .Replace("@@MS_LOGO_W@@", GdFloat(MinespireBar.LogoDesignW))
            .Replace("@@MS_FALLBACK_FONT@@", MinespireBar.FallbackTitleFont.ToString())
            .Replace("@@MS_STEP_FONT@@", MinespireBar.StepFont.ToString())
            .Replace("@@MS_DETAIL_FONT@@", MinespireBar.DetailFont.ToString())
            .Replace("@@MS_LOG_LEFT@@", GdFloat(MinespireBar.LogLeft))
            .Replace("@@MS_LOG_BOTTOM@@", GdFloat(MinespireBar.LogBottom))
            .Replace("@@MS_FOX_W@@", GdFloat(MinespireBar.FoxW))
            .Replace("@@MS_FOX_H@@", GdFloat(MinespireBar.FoxH))
            .Replace("@@MS_FOX_FRAMES@@", MinespireBar.FoxFrames.ToString())
            .Replace("@@MS_FOX_FPS@@", GdFloat(MinespireBar.FoxFps))
            .Replace("@@MS_FOX_RIGHT@@", GdFloat(MinespireBar.FoxRight))
            .Replace("@@MS_FOX_BOTTOM@@", GdFloat(MinespireBar.FoxBottom))
            .Replace("@@MS_VERSION_RIGHT@@", GdFloat(MinespireBar.VersionRight))
            .Replace("@@MS_VERSION_BOTTOM@@", GdFloat(MinespireBar.VersionBottom))
            .Replace("@@MS_CYCLE_S@@", GdFloat(MinespireBar.IndeterminateCycleSeconds))
            .Replace("@@MS_FADE_S@@", GdFloat(MinespireBar.FadeSeconds))
            // Slaytheshin 主题:布局常量在 SlaytheshinBar,模板不写死(同 Minespire)
            .Replace("@@SS_BG_COLOR@@", GdColor(SlaytheshinBar.BgColor))
            .Replace("@@SS_TEXT_COLOR@@", GdColor(SlaytheshinBar.TextColor))
            .Replace("@@SS_LOG_COLOR@@", GdColor(SlaytheshinBar.LogColor))
            .Replace("@@SS_VERSION_COLOR@@", GdColor(SlaytheshinBar.VersionColor))
            .Replace("@@SS_FILL_TINT_COLOR@@", GdColor(SlaytheshinBar.FillTint))
            .Replace("@@SS_DOT_COLOR@@", GdColor(SlaytheshinBar.DotColor))
            .Replace("@@SS_DOT_SCALE@@", GdFloat(SlaytheshinBar.DotScale))
            .Replace("@@SS_PLACEHOLDER_COLOR@@", GdColor(SlaytheshinBar.PlaceholderColor))
            .Replace("@@SS_DESIGN_W@@", GdFloat(SlaytheshinBar.DesignW))
            .Replace("@@SS_DESIGN_H@@", GdFloat(SlaytheshinBar.DesignH))
            .Replace("@@SS_LOGO_Y@@", GdFloat(SlaytheshinBar.LogoY))
            .Replace("@@SS_LOGO_W@@", GdFloat(SlaytheshinBar.LogoDesignW))
            .Replace("@@SS_FALLBACK_FONT@@", SlaytheshinBar.FallbackTitleFont.ToString())
            .Replace("@@SS_ICONS@@", SlaytheshinBar.IconsPerRow.ToString())
            .Replace("@@SS_ICON_SIZE@@", GdFloat(SlaytheshinBar.Row1IconSize))
            .Replace("@@SS_ICON_GAP@@", GdFloat(SlaytheshinBar.Row1Gap))
            .Replace("@@SS_ICON_CY@@", GdFloat(SlaytheshinBar.Row1Cy))
            .Replace("@@SS_ENLARGE@@", GdFloat(SlaytheshinBar.Enlarge))
            .Replace("@@SS_SUB_SCALE@@", GdFloat(SlaytheshinBar.Row2Scale))
            .Replace("@@SS_SUB_GAP@@", GdFloat(SlaytheshinBar.Row2Gap))
            .Replace("@@SS_SUB_CY@@", GdFloat(SlaytheshinBar.Row2Cy))
            .Replace("@@SS_LOG_LINES@@", SlaytheshinBar.LogLines.ToString())
            .Replace("@@SS_LOG_PER_LINE@@", SlaytheshinBar.LogPerLine.ToString())
            .Replace("@@SS_LOG_SEP@@", SlaytheshinBar.LogSeparator)
            .Replace("@@SS_LOG_FONT@@", SlaytheshinBar.LogFont.ToString())
            .Replace("@@SS_LOG_LINE_H@@", GdFloat(SlaytheshinBar.LogLineH))
            .Replace("@@SS_LOG_BOTTOM@@", GdFloat(SlaytheshinBar.LogBottom))
            .Replace("@@SS_LOG_SIDE_PAD@@", GdFloat(SlaytheshinBar.LogSidePad))
            .Replace("@@SS_VERSION_LEFT@@", GdFloat(SlaytheshinBar.VersionLeft))
            .Replace("@@SS_VERSION_TOP@@", GdFloat(SlaytheshinBar.VersionTop))
            .Replace("@@SS_VERSION_FONT@@", GdFloat(SlaytheshinBar.VersionFont));

    /// <summary>Color → GDScript 字面量(不变文化,防区域设置把小数点变逗号)。</summary>
    private static string GdColor(Color c) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Color({c.R:0.####}, {c.G:0.####}, {c.B:0.####}, {c.A:0.####})");

    private static string GdFloat(float value) => value.ToString(
        "0.####", System.Globalization.CultureInfo.InvariantCulture);

    private const string BootSplashGdTemplate = @"extends Node
# LoadingBar boot view — injected by ItsLoading mod. BOOT_VERSION = 23
# 启动时主动自检:mod 在 settings 里被禁用、或本地/工坊文件均已不存在,
# 则不显示任何进度条,并错后 2 秒做原子自清理(避开启动期 I/O;任何时刻被强退均无害)。
# 正常路径按主题(BaseLib cfg @@THEME_CFG_PATH@@;C# ThemeRegistry 读同一文件)分支布局:
#   classic  —— 底部条(无垫底),负责进度刻度 0 → 0.25,
#               尾部增量跟踪 godot.log 显示工坊读取进度;
#   minespire —— 整屏红居中布局(Minecraft 风格,含右下奔跑狐狸),
#               同样覆盖 0 → 0.25,工坊轮询逻辑各布局共用;
#   slaytheshin —— 整屏白居中布局(原神风):两排徽记图标即进度条,第一排
#               当前阶段放大、第二排(图标+间隙小圆)被剪贴蒙版式深色填充
#               从左往右逐渐「灌」深,底部 3 行 × 5 条居中活动日志,进度区不写文字。
# 桥协议(BOOT_VERSION 16 / bridge_version 2):C# 侧经 csharp_attach() 确认接管后,本节点
# 成为唯一加载 UI——工坊轮询/旧 30s 安全网停用,节点保持可见且仅保留 5 分钟失联看门狗;
# 全程呈现改由 C# 侧 csharp_present() 逐事件驱动(与 ClassicBar 同一数学与出帧配对)。
# 退休仍走 takeover()(隐藏图层);版本协商字段 bridge_version(C# 侧见 GdBridgeBar)。

const LOG_PATH := ""user://logs/godot.log""
const FRAC_END := @@WORKSHOP_END@@
const MOD_ID := ""@@MOD_ID@@""
const CLEANUP_DELAY := 2.0

var _layer: CanvasLayer
var _step: Label
var _detail: Label
var _overall_fill: ColorRect
var _local_fill: ColorRect
var _track_w := 0.0
var _t := 0.0
var _elapsed := 0.0
var _done := false
var _cleanup_pending := false
var _cleaned := false
var _log_pos := -1
var _log_buf := """"
var _steam_n := 0
var _seen_ids := {}
var _steam_total := -1
var _poll_acc := 0.0
var boot_start_msec := 0
var _lang := ""eng""
var _strings := {}
var _frozen := false
var _frozen_msec := 0         # 冻结起点:冻结分支安全网从这点起算(_ready 累计会让慢扫描后的合法冻结被秒杀)
var _last_activity_msec := 0  # 最近一次观测到 godot.log 增长的时刻:接管前安全网按「静默」计时,
                              # 而非从 _ready 绝对计时——冷缓存/Steam 元数据慢的工坊扫描可远超 30s
                              # (2026-08-30 实机:扫描 59s,绝对计时网在扫描中途退休了 splash,
                              #  之后 C# 全程 present 被丢弃 → 加载期黑屏)
# ---- 工坊扫描时序(瀑布图的「游戏预加载」块逐项拆解) ----
var _ws_order: Array = []   # [[工坊项id, 首见引擎毫秒], ...](日志到达序)
var _ws_names := {}         # 工坊项id → mod 显示名(清单文件基名)
var _ws_end_msec := 0       # 扫描结束(首见 dll 加载行);0 = 未观测到
# ---- C# 桥状态 ----
var bridge_version := @@BRIDGE_VERSION@@
var _bridge_attached := false
var _local_indeterminate := true
var _bridge_last_present_msec := 0
var _last_stage := 0
var _smooth_progress := false
var _overall_display := 0.0
var _overall_target := 0.0
var _local_display := 0.0
var _local_target := 0.0
# 仅兜底 C# 中途死亡；必须远高于合法的慢云同步/慢 mod 启动。
const BRIDGE_WATCHDOG_MSEC := 300000
const SMOOTH_SPEED := 5.0
# ---- 活动日志(阶段行上方的小字滚动历史) ----
# 同步突发期间主循环不迭代、强制帧不上屏(macOS/Metal 实测):突发里 present
# 推进的一切都不可见,条会从工坊直接跳到资产阶段。日志在突发期间照常积累
# (present 仍在调用),帧恢复后用户看到突发尾部——卡住时加载了什么,有据可查。
const ACTIVITY_LINES := 10
const ACTIVITY_LINE_H := 17.0
const ACTIVITY_FONT := 12
var _log_lines: Array = []
var _log_labels: Array = []
var _last_log := """"
# ---- 主题(BaseLib cfg @@THEME_CFG_PATH@@;C# ThemeRegistry 读同一文件) ----
var _theme := ""classic""
var _fade_root: Control        # 主题全屏根(minespire/slaytheshin):揭幕淡出对它做 modulate(CanvasLayer 无此属性)
var _fill_base_x := 0.0        # 阶段条填充 x 基线(classic 0;minespire 内缩;slaytheshin 第二排左缘;滑块滑过后复位)
var _fox_atlas: AtlasTexture   # 奔跑狐狸逐帧 region(素材缺席则保持 null,全流程跳过)
# ---- slaytheshin 主题状态(白底双排徽记 + 灰遮罩) ----
const SS_LOG_SEP := ""@@SS_LOG_SEP@@""  # 分隔符走 token;必须 const 包裹(裸 token 会被门禁哑替换成 1.0)
# 第二排剪贴蒙版暗层 shader(C# SlaytheshinBar.FillShaderCode 同款):贴图按 tint 暗调,
# 仅轨分数段 [seg_a, seg_b] 内可见;nf_* 构建期烘焙本节点在轨上的分数几何。进度更新只写
# seg_a/seg_b(全节点同值)——set_shader_parameter 走 RenderingServer,不触 Control 矩形,
# 同步突发冻结期照常生效(与第一排放大标记的变换路径同款约束)。
# 三引号用单引号:双引号在宿主 verbatim 字符串里会被成对转义吞噬。
const SS_FILL_GLSL := '''shader_type canvas_item;
uniform float seg_a = 0.0;
uniform float seg_b = 0.0;
uniform float nf_left = 0.0;
uniform float nf_width = 1.0;
uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

void fragment() {
	vec4 c = texture(TEXTURE, UV);
	float x = nf_left + UV.x * nf_width;
	float vis = step(seg_a, x) * step(x, seg_b) * step(0.0001, seg_b - seg_a);
	COLOR = vec4(c.rgb * tint.rgb, c.a * tint.a * vis);
}'''
var _ss_row1: Array = []       # 第一排 7 控件(TextureRect / 缺图占位 ColorRect)
var _ss_row1_rect: Array = []  # 第一排普通尺寸 Rect2(屏幕绝对坐标;放大不改矩形,见 _ss_set_stage)
var _ss_fill_mats: Array = []  # 第二排暗色孪生材质(seg_a/seg_b 段驱动,见 _ss_sync_fill)
var _ss_fill_shader: Shader

func _read_theme() -> void:
	# 读链:cfg 的 Theme 键(枚举名,to_lower 统一)→ 旧 txt(迁移完成前的过渡启动,
	# C# MigrateToCfg 稍后并入并删除)→ classic。
	# 不做白名单:未知 id 落 classic 布局(未来主题 + 旧脚本优雅降级)。
	var p := ""@@THEME_CFG_PATH@@""
	if FileAccess.file_exists(p):
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data is Dictionary and data.get(""Theme"") is String:
			_theme = str(data[""Theme""]).to_lower()
			return
	var legacy := ""@@THEME_LEGACY_PATH@@""
	if FileAccess.file_exists(legacy):
		var s := FileAccess.get_file_as_string(legacy).strip_edges().to_lower()
		if s != """":
			_theme = s

func _ready() -> void:
	boot_start_msec = Time.get_ticks_msec()
	_last_activity_msec = boot_start_msec
	_detect_language()
	_read_theme()
	if _detect_state() != ""ok"":
		_done = true
		_cleanup_pending = true
		print(""[LoadingBarBoot] mod disabled or unsubscribed — bar suppressed, cleanup deferred"")
	else:
		_load_strings()
		_build_ui()
		_skip_log_history()
		# 先显示 0/N,避免首次 0.2s 日志轮询把用户的第一眼直接批到 6/N。
		_steam_total = _count_workshop()
		if _steam_total > 0:
			_set_progress(0, _steam_total, """")
		print(""[LoadingBarBoot] splash ready at frame "", Engine.get_frames_drawn())

func _detect_language() -> void:
	for p in _settings_files():
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data is Dictionary and data.get(""language"") is String:
			_lang = data.get(""language"")
			return

# ---------------- 翻译表(mod 目录 localization/<语言>/strings.json) ----------------
# 与 C# I18n 同一张表、同一条回退链:目标语言 → eng → 键本身。

func _read_strings(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}
	var data = JSON.parse_string(FileAccess.get_file_as_string(path))
	return data if data is Dictionary else {}

func _load_strings() -> void:
	var mod_dir := _mod_dir()
	if mod_dir == """":
		return
	_strings = _read_strings(mod_dir.path_join(""localization/eng/strings.json""))
	if _lang != ""eng"":
		var overlay := _read_strings(mod_dir.path_join(""localization/"" + _lang + ""/strings.json""))
		for k in overlay:
			_strings[k] = overlay[k]

func _txt(key: String) -> String:
	# 不叫 _t:与 shimmer 计时字段 var _t 冲突会让整个脚本解析失败
	return _strings.get(key, key)

# ---------------- 自检与自清理 ----------------

func _detect_state() -> String:
	# 本地与工坊的 mod 文件都不在了 = 退订/移除
	if not _mod_files_present():
		return ""gone""
	# 所有提到本 mod 的 settings.save 都是 disabled = 被关闭
	var mentioned := false
	var enabled_seen := false
	for p in _settings_files():
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data == null or not (data is Dictionary):
			continue
		var ms = data.get(""mod_settings"")
		if ms == null or not (ms is Dictionary):
			continue
		var ml = ms.get(""mod_list"")
		if ml == null or not (ml is Array):
			continue
		for e in ml:
			if e is Dictionary and e.get(""id"") == MOD_ID:
				mentioned = true
				if e.get(""is_enabled"", true):
					enabled_seen = true
	if mentioned and not enabled_seen:
		return ""disabled""
	return ""ok""

func _settings_files() -> Array:
	var out := []
	var d := DirAccess.open(""user://steam"")
	if d:
		d.list_dir_begin()
		var n := d.get_next()
		while n != """":
			var p := ""user://steam/"".path_join(n + ""/settings.save"")
			if d.current_is_dir() and FileAccess.file_exists(p):
				out.append(p)
			n = d.get_next()
		d.list_dir_end()
	return out

# 从可执行文件目录逐级向上探测 workshop/content/2868840:
# macOS 的 .app 布局在上方第 5 级(Contents/MacOS → …/steamapps),
# Windows/Linux 的直接布局在第 3 级(游戏目录 → …/steamapps)。
# 固定走 5 级在 Win/Linux 会高出 Steam 库两级,工坊检测永远失败。
func _workshop_root() -> String:
	var d := OS.get_executable_path().get_base_dir()
	for i in range(8):
		var root := d.path_join(""workshop/content/2868840"")
		if DirAccess.dir_exists_absolute(root):
			return root
		var parent := d.get_base_dir()
		if parent == d:
			return """"
		d = parent
	return """"

func _mod_dir() -> String:
	# 本地安装或工坊条目目录(含 ItsLoading.json 的那层);翻译表在其 localization/ 下
	var exe_dir := OS.get_executable_path().get_base_dir()
	var local := exe_dir.path_join(""mods/"" + MOD_ID)
	if FileAccess.file_exists(local.path_join(MOD_ID + "".json"")):
		return local
	var ws_root := _workshop_root()
	if ws_root == """":
		return """"
	var ws := DirAccess.open(ws_root)
	if ws:
		ws.list_dir_begin()
		var n := ws.get_next()
		while n != """":
			if ws.current_is_dir() and FileAccess.file_exists(ws_root.path_join(n + ""/"" + MOD_ID + "".json"")):
				ws.list_dir_end()
				return ws_root.path_join(n)
			n = ws.get_next()
		ws.list_dir_end()
	return """"

func _mod_files_present() -> bool:
	return _mod_dir() != """"

# 错后清理:①临时文件+rename 原子替换 override.cfg ②删脚本。
# 任何时刻被强退:2 秒内 = 零写入;①之后 = cfg 已干净,gd 文件惰性无害。
func _do_cleanup() -> void:
	_cleaned = true
	var exe_dir := OS.get_executable_path().get_base_dir()
	var cfg := exe_dir.path_join(""override.cfg"")
	if FileAccess.file_exists(cfg):
		var filtered := _cfg_without_us(FileAccess.get_file_as_string(cfg))
		var tmp := ""override.cfg.lbnew""
		var w := FileAccess.open(exe_dir.path_join(tmp), FileAccess.WRITE)
		if w:
			w.store_string(filtered)
			w.close()
			var dir := DirAccess.open(exe_dir)
			if dir == null or dir.rename(tmp, ""override.cfg"") != OK:
				var w2 := FileAccess.open(cfg, FileAccess.WRITE)
				if w2:
					w2.store_string(filtered)
					w2.close()
	for f in [""@@GD_USER_PATH@@""]:
		if FileAccess.file_exists(f):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(f))
	print(""[LoadingBarBoot] self-cleanup complete"")

# 移除我们的标记行与 autoload 条目;若 [autoload] 段因此为空则连段头一并移除。
func _cfg_without_us(s: String) -> String:
	var pass1 := PackedStringArray()
	for line in s.split(""\n""):
		var t := line.strip_edges()
		if t.begins_with("";"") and ""@@CFG_MARKER@@"" in t:
			continue
		if ""@@AUTOLOAD_NAME@@"" in t and t.find(""="") > -1:
			continue
		pass1.append(line)
	var out := PackedStringArray()
	var in_autoload := false
	var autoload_empty := true
	var header_idx := -1
	for i in pass1.size():
		var t := pass1[i].strip_edges()
		if t.begins_with(""[""):
			if in_autoload and header_idx >= 0 and autoload_empty:
				out.remove_at(header_idx)
				header_idx = -1
			in_autoload = t.to_lower() == ""[autoload]""
			if in_autoload:
				header_idx = out.size()
				autoload_empty = true
		else:
			if in_autoload and t != """" and not t.begins_with("";"") and not t.begins_with(""#""):
				autoload_empty = false
		out.append(pass1[i])
	if in_autoload and header_idx >= 0 and autoload_empty:
		out.remove_at(header_idx)
	return ""\n"".join(out)

# ---------------- UI(无垫底) ----------------

func _build_ui() -> void:
	var vs: Vector2 = get_viewport().get_visible_rect().size
	_layer = CanvasLayer.new()
	# 正常路径中本层从帧 0 持续到菜单就绪;998 仅让首启/版本不匹配时的
	# C# 兜底条(999)可以无闪烁覆盖它。
	_layer.layer = 998
	add_child(_layer)
	if _theme == ""minespire"":
		_build_ui_minespire(vs)
	elif _theme == ""slaytheshin"":
		_build_ui_slaytheshin(vs)
	else:
		_build_ui_classic(vs)

func _build_ui_classic(vs: Vector2) -> void:
	var strip := Control.new()
	strip.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	strip.offset_top = -@@STRIP_HEIGHT@@
	_layer.add_child(strip)

	_step = Label.new()
	_step.position = Vector2(@@PAD@@, @@STEP_Y@@)
	_step.add_theme_font_size_override(""font_size"", 20)
	_step.add_theme_color_override(""font_color"", Color.WHITE)
	_step.text = _txt(""bar.starting"")
	strip.add_child(_step)

	_detail = Label.new()
	_detail.position = Vector2(@@PAD@@, @@DETAIL_Y@@)
	_detail.add_theme_font_size_override(""font_size"", 14)
	_detail.add_theme_color_override(""font_color"", @@DETAIL_COLOR@@)
	_detail.text = ""engine boot""
	strip.add_child(_detail)

	_track_w = vs.x - @@PAD@@ * 2.0
	_overall_fill = _add_bar(strip, @@OVERALL_Y@@, @@OVERALL_HEIGHT@@, @@OVERALL_FILL_COLOR@@)
	_local_fill = _add_bar(strip, @@LOCAL_Y@@, @@LOCAL_HEIGHT@@, @@FILL_COLOR@@)

	# 活动日志:条带上方、向上滚动;越旧越淡。负 y = 在条带上边缘之上
	# (Control 默认不裁剪子节点)。
	var base_col: Color = @@DETAIL_COLOR@@
	for i in ACTIVITY_LINES:
		var l := Label.new()
		l.position = Vector2(@@PAD@@, -(float(ACTIVITY_LINES - i) * ACTIVITY_LINE_H + 4.0))
		l.add_theme_font_size_override(""font_size"", ACTIVITY_FONT)
		l.add_theme_color_override(""font_color"",
			Color(base_col.r, base_col.g, base_col.b, 0.3 + 0.65 * float(i + 1) / ACTIVITY_LINES))
		strip.add_child(l)
		_log_labels.append(l)

# ---------------- Minespire 主题布局(整屏红,854×480 设计矩形等比缩放居中) ----------------
# 两布局产出同名引用(_step/_detail/_overall_fill/_local_fill/_track_w/_log_labels),
# 轮询/桥/看门狗/冻结逻辑因此零分支。仍是全手动定位、不用 Container(同步突发期铁律)。

func _build_ui_minespire(vs: Vector2) -> void:
	var s: float = min(vs.x / @@MS_DESIGN_W@@, vs.y / @@MS_DESIGN_H@@)
	var ox: float = (vs.x - @@MS_DESIGN_W@@ * s) * 0.5
	var oy: float = (vs.y - @@MS_DESIGN_H@@ * s) * 0.5
	_fill_base_x = @@MS_FILL_INSET@@ * s

	# 全屏根 + 红底:整屏覆盖期菜单仍可交互(全家 IGNORE,纯视觉覆盖)
	_fade_root = Control.new()
	_fade_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_fade_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_layer.add_child(_fade_root)
	var bg := ColorRect.new()
	bg.color = @@MS_BG_COLOR@@
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(bg)

	_ms_add_logo(s, ox, oy)

	# 条块:step 标签 → 总体条 → detail 标签 → 阶段条(Minecraft 风格 labelGap/barGap 流)
	var bar_x: float = ox + (@@MS_DESIGN_W@@ * 0.5 - @@MS_BAR_W@@ * 0.5) * s
	var y: float = oy + @@MS_BARS_TOP@@ * s
	_step = Label.new()
	_step.position = Vector2(bar_x, y)
	_step.add_theme_font_size_override(""font_size"", int(round(@@MS_STEP_FONT@@ * s)))
	_step.add_theme_color_override(""font_color"", @@MS_TEXT_COLOR@@)
	_step.text = _txt(""bar.starting"")
	_step.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(_step)
	y += (@@MS_STEP_LABEL_H@@ + @@MS_LABEL_GAP@@) * s
	_overall_fill = _ms_add_bar(Vector2(bar_x, y), s)
	_overall_fill.color = Color(1, 1, 1, 0.75)
	y += (@@MS_BAR_H@@ + @@MS_BAR_GAP@@) * s
	_detail = Label.new()
	_detail.position = Vector2(bar_x, y)
	_detail.add_theme_font_size_override(""font_size"", int(round(@@MS_DETAIL_FONT@@ * s)))
	_detail.add_theme_color_override(""font_color"", @@MS_DIM_COLOR@@)
	_detail.text = ""engine boot""
	_detail.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(_detail)
	y += (@@MS_DETAIL_LABEL_H@@ + @@MS_LABEL_GAP@@) * s
	_local_fill = _ms_add_bar(Vector2(bar_x, y), s)
	# 填充契约同 classic:fill 宽 = _track_w × 分数;minespire 的 _track_w 是内缩后的净宽
	_track_w = (@@MS_BAR_W@@ - 2.0 * @@MS_FILL_INSET@@) * s

	# 活动日志:左下角,最新在底部,越旧越淡(行高/字号常量与 classic 共用)
	for i in ACTIVITY_LINES:
		var l := Label.new()
		l.position = Vector2(ox + @@MS_LOG_LEFT@@ * s,
				oy + (@@MS_DESIGN_H@@ - @@MS_LOG_BOTTOM@@ - float(ACTIVITY_LINES - i) * ACTIVITY_LINE_H) * s)
		l.add_theme_font_size_override(""font_size"", int(round(ACTIVITY_FONT * s)))
		l.add_theme_color_override(""font_color"",
			Color(1, 1, 1, 0.3 + 0.65 * float(i + 1) / ACTIVITY_LINES))
		l.mouse_filter = Control.MOUSE_FILTER_IGNORE
		_fade_root.add_child(l)
		_log_labels.append(l)

	_ms_add_fox(s, ox, oy)
	_ms_add_version(s, ox, oy)

# 主题素材统一加载(mod 目录;缺席/损坏返回 null,调用方各自优雅降级)
func _load_texture(path: String) -> ImageTexture:
	if not FileAccess.file_exists(path):
		return null
	var img := Image.new()
	if img.load_png_from_buffer(FileAccess.get_file_as_bytes(path)) != OK:
		return null
	return ImageTexture.create_from_image(img)

func _ms_add_logo(s: float, ox: float, oy: float) -> void:
	# MC 风格游戏 logo(设计宽 @@MS_LOGO_W@@、高按图比例,水平居中);
	# 素材缺席回退同位文字标题——主题不因缺图失败。
	var tex: ImageTexture = null
	var mod_dir := _mod_dir()
	if mod_dir != """":
		tex = _load_texture(mod_dir.path_join(""mc_style_sts2_logo.png""))
	if tex == null:
		var title := Label.new()
		title.text = ""SLAY THE SPIRE 2""
		title.position = Vector2(ox, oy + @@MS_LOGO_Y@@ * s)
		title.size = Vector2(@@MS_DESIGN_W@@ * s, (@@MS_FALLBACK_FONT@@ + 6.0) * s)
		title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		title.add_theme_font_size_override(""font_size"", int(round(@@MS_FALLBACK_FONT@@ * s)))
		title.add_theme_color_override(""font_color"", @@MS_TEXT_COLOR@@)
		title.mouse_filter = Control.MOUSE_FILTER_IGNORE
		_fade_root.add_child(title)
		return
	var w: float = @@MS_LOGO_W@@ * s
	var h: float = w * tex.get_height() / max(1.0, float(tex.get_width()))
	var logo := TextureRect.new()
	# 钳制陷阱:默认 KEEP_SIZE 的最小尺寸=贴图尺寸;texture/
	# position 赋值把控件顶到该最小尺寸后,size 再赋也会被旧 min 钳住(2046px
	# logo 原样进小窗口)。必须在设 texture 之前 IGNORE_SIZE,最小尺寸全程为 0。
	logo.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	logo.texture = tex
	logo.position = Vector2(ox + (@@MS_DESIGN_W@@ * s - w) * 0.5, oy + @@MS_LOGO_Y@@ * s)
	logo.stretch_mode = TextureRect.STRETCH_SCALE
	logo.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
	logo.mouse_filter = Control.MOUSE_FILTER_IGNORE
	logo.size = Vector2(w, h)
	_fade_root.add_child(logo)

func _ms_add_bar(pos: Vector2, s: float) -> ColorRect:
	# 2px 白描边空心 + 内缩 4px 白填充:Minecraft 风格 nine-slice 条的逐像素复刻
	# (progress_bar_bg/fg 本就是纯二色像素画,无需贴图)
	var sb := StyleBoxFlat.new()
	sb.border_color = @@MS_TEXT_COLOR@@
	sb.draw_center = false
	sb.set_border_width_all(int(round(@@MS_BORDER_W@@ * s)))
	var outline := Panel.new()
	outline.position = pos
	outline.size = Vector2(@@MS_BAR_W@@ * s, @@MS_BAR_H@@ * s)
	outline.mouse_filter = Control.MOUSE_FILTER_IGNORE
	outline.add_theme_stylebox_override(""panel"", sb)
	_fade_root.add_child(outline)
	var fill := ColorRect.new()
	fill.position = Vector2(@@MS_FILL_INSET@@ * s, @@MS_FILL_INSET@@ * s)
	fill.size = Vector2(0, (@@MS_BAR_H@@ - 2.0 * @@MS_FILL_INSET@@) * s)
	fill.color = @@MS_TEXT_COLOR@@
	fill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	outline.add_child(fill)
	return fill

func _ms_add_fox(s: float, ox: float, oy: float) -> void:
	# 奔跑狐狸(© NeoForged contributors, LGPL-2.1,FancyModLoader):mod 目录内的
	# 竖排 28 帧精灵,驱动在 _process(自然帧,突发期自动冻结)。素材缺席只少一只狐狸。
	var mod_dir := _mod_dir()
	if mod_dir == """":
		return
	var sheet := _load_texture(mod_dir.path_join(""fox_running.png""))
	if sheet == null:
		return
	_fox_atlas = AtlasTexture.new()
	_fox_atlas.atlas = sheet
	_fox_atlas.region = Rect2(0, 0, @@MS_FOX_W@@, @@MS_FOX_H@@)
	var fox := TextureRect.new()
	fox.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 同 logo:须在 texture 之前,防最小尺寸钳制(s<1 时狐狸也中招)
	fox.texture = _fox_atlas
	fox.position = Vector2(ox + (@@MS_DESIGN_W@@ - @@MS_FOX_RIGHT@@ - @@MS_FOX_W@@) * s,
			oy + (@@MS_DESIGN_H@@ - @@MS_FOX_BOTTOM@@ - @@MS_FOX_H@@) * s)
	fox.stretch_mode = TextureRect.STRETCH_SCALE
	fox.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
	fox.mouse_filter = Control.MOUSE_FILTER_IGNORE
	fox.size = Vector2(@@MS_FOX_W@@ * s, @@MS_FOX_H@@ * s)
	_fade_root.add_child(fox)

func _ms_add_version(s: float, ox: float, oy: float) -> void:
	var ver := Label.new()
	ver.text = ""It's Loading v@@MOD_VERSION@@""
	ver.position = Vector2(ox + (@@MS_DESIGN_W@@ - @@MS_VERSION_RIGHT@@ - 300.0) * s,
			oy + (@@MS_DESIGN_H@@ - @@MS_VERSION_BOTTOM@@ - 16.0) * s)
	ver.size = Vector2(300.0 * s, 16.0 * s)
	ver.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	ver.add_theme_font_size_override(""font_size"", int(round(12.0 * s)))
	ver.add_theme_color_override(""font_color"", @@MS_DIM_COLOR@@)
	ver.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(ver)

func _slide_local() -> void:
	# 不定进度语义(minespire/slaytheshin 共用):1/4 宽滑块左→右滚动一周(非回弹),
	# 驱动在 _process。slaytheshin 里 _local_fill 是游离哑对象——真正的可视是
	# _ss_sync_fill() 的滑段(同一公式、同一蒙版形状)。
	_local_fill.size.x = _track_w * 0.25
	_local_fill.position.x = _fill_base_x + fposmod(_t / @@MS_CYCLE_S@@, 1.0) * _track_w * 0.75
	_ss_sync_fill()  # slaytheshin:滑段参数化到孪生材质(其余主题空表 no-op)

# ---------------- Slaytheshin 主题布局(整屏白,854×480 设计矩形等比缩放居中) ----------------
# 原神风:两排徽记图标就是进度条。同名引用契约的关键映射——
#   _local_fill = 游离哑 ColorRect(不入树)、_track_w = 第二排总跨度、
#   _fill_base_x = 第二排左缘 x,于是 _set_progress / csharp_present / _process 平滑与
#   不定分支零改动照常写宽度;第二排真正的可视由 _ss_fill_mats 的段参数承担
#   (_ss_sync_fill 在各写入点后同步)。_step/_detail/_overall_fill 同为游离哑对象
#   ——只喂共享逻辑的 null 守卫与文本/宽度写入。第一排(总进度)当前阶段
#   @@SS_ENLARGE@@× 放大(_ss_set_stage,换阶段才动;pivot=底边中点 + scale,只写变换、
#   永不改矩形——底边与整排对齐、原地放大);第二排(当前进度)85% 尺寸更密,
#   图标+间隙小圆被剪贴蒙版式深色填充从左往右逐渐「灌」深。进度区不写文字。

func _build_ui_slaytheshin(vs: Vector2) -> void:
	var s: float = min(vs.x / @@SS_DESIGN_W@@, vs.y / @@SS_DESIGN_H@@)
	var ox: float = (vs.x - @@SS_DESIGN_W@@ * s) * 0.5
	var oy: float = (vs.y - @@SS_DESIGN_H@@ * s) * 0.5

	_fade_root = Control.new()
	_fade_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_fade_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_layer.add_child(_fade_root)
	var bg := ColorRect.new()
	bg.color = @@SS_BG_COLOR@@
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	bg.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(bg)

	_ss_add_logo(s, ox, oy)

	# 第二排(当前进度):小而密;基圆与暗色孪生层随后叠上,行1 最后(孪生只属于第二排)
	var s2: float = @@SS_ICON_SIZE@@ * @@SS_SUB_SCALE@@
	var span2: float = @@SS_ICONS@@ * s2 + (@@SS_ICONS@@ - 1.0) * @@SS_SUB_GAP@@
	var x2: float = (@@SS_DESIGN_W@@ - span2) * 0.5
	# 剪贴蒙版 = 图标+小圆是蒙版形状,深色内容(基图 × @@SS_FILL_TINT_COLOR@@,徽记 50% 灰 →
	# 75% 深灰)只在轨分数段 [seg_a, seg_b] 内可见,段随 local 从左往右长 → 图标与小圆
	# 被逐渐「灌」深。几何全用轨分数(shader 内映射),进度只走 set_shader_parameter
	# (见 _ss_sync_fill)——不触 Control 矩形,同步突发冻结期照常生效。
	_ss_fill_shader = Shader.new()
	_ss_fill_shader.code = SS_FILL_GLSL
	var circle: ImageTexture = _ss_circle_tex()
	var white: ImageTexture = _ss_white_tex()
	var dot: float = s2 * @@SS_DOT_SCALE@@
	for j in range(@@SS_ICONS@@ - 1):
		var dcx: float = x2 + (float(j) + 1.0) * s2 + float(j) * @@SS_SUB_GAP@@ + @@SS_SUB_GAP@@ * 0.5
		var drect := _ss_center_rect(ox, oy, s, dcx, @@SS_SUB_CY@@, dot)
		var base_dot := TextureRect.new()
		base_dot.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:须在 texture 之前
		base_dot.texture = circle
		base_dot.stretch_mode = TextureRect.STRETCH_SCALE
		base_dot.modulate = @@SS_DOT_COLOR@@  # 常驻浅灰小圆(50%,与徽记同灰阶)
		base_dot.position = drect.position
		base_dot.size = drect.size
		base_dot.mouse_filter = Control.MOUSE_FILTER_IGNORE
		_fade_root.add_child(base_dot)
		_ss_add_fill(circle, drect, (dcx - dot * 0.5 - x2) / span2, dot / span2,
				@@SS_DOT_COLOR@@ * @@SS_FILL_TINT_COLOR@@)
	for i in range(@@SS_ICONS@@):
		var irect := _ss_center_rect(ox, oy, s, x2 + i * (s2 + @@SS_SUB_GAP@@) + s2 * 0.5, @@SS_SUB_CY@@, s2)
		_ss_add_icon(_ss_icon_path(@@SS_ICONS@@ + i + 1), irect)
		# 孪生基色 = 白(缺图白方块占位)或徽记纹理自身灰阶,×@@SS_FILL_TINT_COLOR@@ 后同为 75% 深灰档
		var itex := _ss_tex_or_null(_ss_icon_path(@@SS_ICONS@@ + i + 1))
		_ss_add_fill(itex if itex != null else white, irect,
				float(i) * (s2 + @@SS_SUB_GAP@@) / span2, s2 / span2, @@SS_FILL_TINT_COLOR@@)
	# 哑引用:共享写入点(csharp_present/_set_progress/_process/_slide_local)仍写
	# _local_fill 的 size/position——游离对象无渲染副作用(同 _overall_fill 契约);
	# 第二排可视改由 _ss_fill_mats 段参数承担,_fill_base_x/_track_w 仅供这些写入。
	_local_fill = ColorRect.new()
	_fill_base_x = ox + x2 * s
	_track_w = span2 * s

	# 第一排(总进度):normal 矩形表一次预计算,矩形终身不变——放大是 pivot+scale
	# (见 _ss_set_stage),底边中点为不动点,底边与整排对齐、原地向上/两侧长大。
	var row_bottom: float = @@SS_ICON_CY@@ + @@SS_ICON_SIZE@@ * 0.5
	var span1: float = @@SS_ICONS@@ * @@SS_ICON_SIZE@@ + (@@SS_ICONS@@ - 1.0) * @@SS_ICON_GAP@@
	var x1: float = (@@SS_DESIGN_W@@ - span1) * 0.5
	for i in range(@@SS_ICONS@@):
		var cx: float = x1 + i * (@@SS_ICON_SIZE@@ + @@SS_ICON_GAP@@) + @@SS_ICON_SIZE@@ * 0.5
		_ss_row1_rect.append(_ss_bottom_rect(ox, oy, s, cx, row_bottom, @@SS_ICON_SIZE@@))
		_ss_row1.append(_ss_add_icon(_ss_icon_path(i + 1), _ss_row1_rect[i]))
	_ss_set_stage(1)  # 工坊期(阶段 1)起即有放大标记

	# 底部居中 3 行 × 每行 5 条活动日志(整行淘汰,渲染在 _ss_log_render)
	for i in range(@@SS_LOG_LINES@@):
		var l := Label.new()
		l.position = Vector2(ox + @@SS_LOG_SIDE_PAD@@ * s,
				oy + (@@SS_DESIGN_H@@ - @@SS_LOG_BOTTOM@@ - float(@@SS_LOG_LINES@@ - i) * @@SS_LOG_LINE_H@@) * s)
		l.size = Vector2((@@SS_DESIGN_W@@ - 2.0 * @@SS_LOG_SIDE_PAD@@) * s, @@SS_LOG_LINE_H@@ * s)
		l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		l.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
		l.add_theme_font_size_override(""font_size"", int(round(@@SS_LOG_FONT@@ * s)))
		l.add_theme_color_override(""font_color"", @@SS_LOG_COLOR@@)
		l.mouse_filter = Control.MOUSE_FILTER_IGNORE
		_fade_root.add_child(l)
		_log_labels.append(l)

	_ss_add_version(s, ox, oy)

	# 哑引用:共享逻辑(csharp_present 空判 / _set_progress 文本与宽度写入)需要它们非空。
	# 不入树的游离对象,写入无渲染副作用;会话级 3 个小对象,不释放。
	_step = Label.new()
	_detail = Label.new()
	_overall_fill = ColorRect.new()

func _ss_icon_path(index: int) -> String:
	return ""slaytheshin_%d.png"" % index

func _ss_center_rect(ox: float, oy: float, s: float, cx: float, cy: float, size: float) -> Rect2:
	return Rect2(ox + (cx - size * 0.5) * s, oy + (cy - size * 0.5) * s, size * s, size * s)

# 底边中点锚:放大时底边不动、原地向上/两侧长大(与 C# BottomCenterRect 同款)
func _ss_bottom_rect(ox: float, oy: float, s: float, cx: float, bottom: float, size: float) -> Rect2:
	return Rect2(ox + (cx - size * 0.5) * s, oy + (bottom - size) * s, size * s, size * s)

# 主题贴图解析:dll 同目录(与 C# LoadThemeTexture 同一处);缺席返回 null
func _ss_tex_or_null(path: String) -> ImageTexture:
	var mod_dir := _mod_dir()
	if mod_dir != """":
		return _load_texture(mod_dir.path_join(path))
	return null

# 单槽位:贴图缺席 → 灰方块占位(布局数学不变,主题不因缺图失败)
func _ss_add_icon(path: String, rect: Rect2) -> Control:
	var tex := _ss_tex_or_null(path)
	var c: Control
	if tex != null:
		var t := TextureRect.new()
		t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:须在 texture 之前
		t.texture = tex
		t.stretch_mode = TextureRect.STRETCH_SCALE
		c = t
	else:
		var ph := ColorRect.new()
		ph.color = @@SS_PLACEHOLDER_COLOR@@
		c = ph
	c.position = rect.position
	c.size = rect.size
	c.pivot_offset = Vector2(rect.size.x * 0.5, rect.size.y)  # 底边中点 = scale 的不动点
	c.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(c)
	return c

# 暗色孪生节点:与基图同 rect,shader 按 tint 暗调、仅轨分数段 [seg_a, seg_b] 内可见
# (PS 剪贴蒙版的深色内容)。构建期写死矩形与几何(nf_*);进度只走 set_shader_parameter
# (_ss_sync_fill),不触 Control 矩形。
func _ss_add_fill(tex: Texture2D, rect: Rect2, nf_left: float, nf_width: float, tint: Color) -> void:
	var t := TextureRect.new()
	t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:须在 texture 之前
	t.texture = tex
	t.stretch_mode = TextureRect.STRETCH_SCALE
	t.position = rect.position
	t.size = rect.size
	t.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var mat := ShaderMaterial.new()
	mat.shader = _ss_fill_shader
	mat.set_shader_parameter(""tint"", tint)
	mat.set_shader_parameter(""nf_left"", nf_left)
	mat.set_shader_parameter(""nf_width"", nf_width)
	mat.set_shader_parameter(""seg_a"", 0.0)
	mat.set_shader_parameter(""seg_b"", -1.0)  # 空段:首帧填充前不可见
	t.material = mat
	_fade_root.add_child(t)
	_ss_fill_mats.append(mat)

# 第二排填充段(与 C# SlaytheshinFill.Segment 同公式):确定进度 = [0, _local_display];
# 不定进度 = 1/4 宽滑段,头部 fposmod(_t / @@MS_CYCLE_S@@) × 0.75(与 _slide_local 同式)。
# 段参数全体节点同值;其余主题(空表)no-op。
func _ss_sync_fill() -> void:
	if _ss_fill_mats.is_empty():
		return
	var a: float
	var b: float
	if _local_indeterminate:
		a = fposmod(_t / @@MS_CYCLE_S@@, 1.0) * 0.75
		b = a + 0.25
	else:
		a = 0.0
		b = clampf(_local_display, 0.0, 1.0)
	for m in _ss_fill_mats:
		m.set_shader_parameter(""seg_a"", a)
		m.set_shader_parameter(""seg_b"", b)

# 生成的小圆纹理(纯白,alpha 边缘 1px 抗锯齿;着色走 modulate / 孪生 tint)
func _ss_circle_tex() -> ImageTexture:
	var size := 32
	var r := float(size) * 0.45  # 有效半径 45%:留 1px 淡出带
	var c := (float(size) - 1.0) * 0.5
	var img := Image.create_empty(size, size, false, Image.FORMAT_RGBA8)
	for y in size:
		for x in size:
			var d: float = Vector2(float(x) - c, float(y) - c).length()
			img.set_pixel(x, y, Color(1.0, 1.0, 1.0, clampf(r - d + 0.5, 0.0, 1.0)))
	return ImageTexture.create_from_image(img)

# 缺图占位槽的孪生用纯白小方块(着色 = @@SS_PLACEHOLDER_COLOR@@ × tint,与基占位同构)
func _ss_white_tex() -> ImageTexture:
	var img := Image.create_empty(4, 4, false, Image.FORMAT_RGBA8)
	img.fill(Color.WHITE)
	return ImageTexture.create_from_image(img)

# 第一排放大标记:当前阶段 scale=@@SS_ENLARGE@@、其余 1.0。矩形终身不变(见上方 _ss_row1 注释)。
# 只写变换路径(position 同款,实测突发冻结期照常生效),永不触发「改尺寸→等重绘」。
func _ss_set_stage(stage: int) -> void:
	if _ss_row1.is_empty():
		return
	var idx: int = clamp(stage, 1, @@SS_ICONS@@) - 1
	for i in range(_ss_row1.size()):
		_ss_row1[i].scale = Vector2(@@SS_ENLARGE@@, @@SS_ENLARGE@@) if i == idx else Vector2.ONE

# 3×5 整行淘汰窗口(C# SlaytheshinLog 同款算法):扁平整列表超限 slice 整行,分块渲染
func _ss_log_render() -> void:
	var cap := @@SS_LOG_LINES@@ * @@SS_LOG_PER_LINE@@
	while _log_lines.size() > cap:
		_log_lines = _log_lines.slice(@@SS_LOG_PER_LINE@@)
	for i in _log_labels.size():
		var start := i * @@SS_LOG_PER_LINE@@
		if start >= _log_lines.size():
			_log_labels[i].text = """"
			continue
		var chunk := _log_lines.slice(start, start + @@SS_LOG_PER_LINE@@)
		_log_labels[i].text = SS_LOG_SEP.join(PackedStringArray(chunk))

func _ss_add_logo(s: float, ox: float, oy: float) -> void:
	# 同 minespire 槽位(设计宽 @@SS_LOGO_W@@、高按图比例,水平居中);缺席回退同位文字标题
	var tex: ImageTexture = null
	var mod_dir := _mod_dir()
	if mod_dir != """":
		tex = _load_texture(mod_dir.path_join(""slaytheshin_logo.png""))
	if tex == null:
		var title := Label.new()
		title.text = ""SLAY THE SPIRE 2""
		title.position = Vector2(ox, oy + @@SS_LOGO_Y@@ * s)
		title.size = Vector2(@@SS_DESIGN_W@@ * s, (@@SS_FALLBACK_FONT@@ + 6.0) * s)
		title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		title.add_theme_font_size_override(""font_size"", int(round(@@SS_FALLBACK_FONT@@ * s)))
		title.add_theme_color_override(""font_color"", @@SS_TEXT_COLOR@@)
		title.mouse_filter = Control.MOUSE_FILTER_IGNORE
		_fade_root.add_child(title)
		return
	var w: float = @@SS_LOGO_W@@ * s
	var h: float = w * tex.get_height() / max(1.0, float(tex.get_width()))
	var logo := TextureRect.new()
	logo.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:须在 texture 之前
	logo.texture = tex
	logo.position = Vector2(ox + (@@SS_DESIGN_W@@ * s - w) * 0.5, oy + @@SS_LOGO_Y@@ * s)
	logo.stretch_mode = TextureRect.STRETCH_SCALE
	logo.mouse_filter = Control.MOUSE_FILTER_IGNORE
	logo.size = Vector2(w, h)
	_fade_root.add_child(logo)

func _ss_add_version(s: float, ox: float, oy: float) -> void:
	# 左上角小字:比日志字稍小,15% 灰(默认左对齐,无需 size)
	var ver := Label.new()
	ver.text = ""It's Loading v@@MOD_VERSION@@""
	ver.position = Vector2(ox + @@SS_VERSION_LEFT@@ * s, oy + @@SS_VERSION_TOP@@ * s)
	ver.add_theme_font_size_override(""font_size"", int(round(@@SS_VERSION_FONT@@ * s)))
	ver.add_theme_color_override(""font_color"", @@SS_VERSION_COLOR@@)
	ver.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_fade_root.add_child(ver)

func _add_bar(strip: Control, y: float, height: float, fill_color: Color) -> ColorRect:
	var track := ColorRect.new()
	track.position = Vector2(@@PAD@@, y)
	track.size = Vector2(_track_w, height)
	track.color = @@TRACK_COLOR@@
	strip.add_child(track)
	var fill := ColorRect.new()
	fill.position = Vector2.ZERO
	fill.size = Vector2(0, height)
	fill.color = fill_color
	track.add_child(fill)
	return fill

func _skip_log_history() -> void:
	var f := FileAccess.open(LOG_PATH, FileAccess.READ)
	if f:
		_log_pos = f.get_length()

func _process(delta: float) -> void:
	_elapsed += delta
	if _cleanup_pending and not _cleaned and _elapsed >= CLEANUP_DELAY:
		_do_cleanup()
	# 狐狸逐帧:放在所有早退路径之前(自然帧驱动;突发期 _process 不跑 → 冻结,
	# 与 shimmer 同语义)。retire 后不再推进(_done 时画面本就将隐)。
	if _fox_atlas != null and not _done:
		_fox_atlas.region = Rect2(0.0,
			float(int(_elapsed * @@MS_FOX_FPS@@) % int(@@MS_FOX_FRAMES@@)) * @@MS_FOX_H@@,
			@@MS_FOX_W@@, @@MS_FOX_H@@)
	if _done:
		return
	if _bridge_attached:
		# C# 正常驱动两条;无可测局部总量时只在自然帧上跑轻量动画。
		_t += delta
		if _local_indeterminate:
			if _theme == ""minespire"" or _theme == ""slaytheshin"":
				_slide_local()
			else:
				var w: float = @@PULSE_MIN@@ + abs(fmod(_t * 0.8, 2.0) - 1.0) * @@PULSE_TRAVEL@@
				_local_fill.size.x = min(w, _track_w)
		elif _smooth_progress:
			_overall_display = move_toward(_overall_display, _overall_target, delta * SMOOTH_SPEED)
			_local_display = move_toward(_local_display, _local_target, delta * SMOOTH_SPEED)
			_overall_fill.size.x = _track_w * _overall_display
			_local_fill.size.x = _track_w * _local_display
			_local_fill.position.x = _fill_base_x
			_ss_sync_fill()  # slaytheshin:平滑后的段参数(其余主题空表 no-op)
		if Time.get_ticks_msec() - _bridge_last_present_msec > BRIDGE_WATCHDOG_MSEC:
			print(""[LoadingBarBoot] bridge watchdog expired — dismissing stale boot view"")
			takeover()
		return
	if _frozen:
		# 同步突发已开始(首个 mod dll 加载,帧停止流动):冻结一切 UI 变更。
		# 阻塞瞬间若存在未提交的文字变更(字形重排异步),已呈现帧会被渲染端
		# 失效 → prelude 与 C# 条之间黑屏(实测间歇复现)。60s 安全网仍生效,
		# 但从冻结起点起算——前置阶段(工坊扫描)合法地可超 30s,_ready 累计会误杀。
		if _frozen_msec > 0 and Time.get_ticks_msec() - _frozen_msec > 60000:
			takeover()
		return
	_t += delta
	_poll_acc += delta
	if _poll_acc >= 0.1:
		_poll_acc = 0.0
		_poll_log()
	if _steam_total <= 0:
		if _theme == ""minespire"" or _theme == ""slaytheshin"":
			_slide_local()
		else:
			var w: float = @@PULSE_MIN@@ + abs(fmod(_t * 0.8, 2.0) - 1.0) * @@PULSE_TRAVEL@@
			_local_fill.size.x = min(w, _track_w)
	# 接管前安全网:60s「日志静默」才退休(C# 死了/游戏卡死),扫描慢不算——
	# 日志还在增长就说明启动在进行(_poll_log 刷新 _last_activity_msec)。
	if Time.get_ticks_msec() - _last_activity_msec > 60000:
		print(""[LoadingBarBoot] pre-bridge net: 60s log silence — dismissing"")
		takeover()

func _set_progress(n: int, total: int, detail: String) -> void:
	if total > 0 and n <= total:
		var local := float(n) / float(total)
		_overall_display = FRAC_END * local
		_overall_target = _overall_display
		_local_display = local
		_local_target = local
		_overall_fill.size.x = _track_w * _overall_display
		_local_fill.size.x = _track_w * _local_display
		_local_fill.position.x = _fill_base_x
		_ss_sync_fill()  # slaytheshin:工坊期段的即时提交(其余主题空表 no-op)
		var name := _txt(""bar.workshop"").replace(""{n}"", str(n)).replace(""{t}"", str(total))
		_step.text = _stage_text(1, name)
		_detail.text = detail

func _poll_log() -> void:
	if _log_pos < 0:
		return
	var f := FileAccess.open(LOG_PATH, FileAccess.READ)
	if f == null:
		return
	var size := f.get_length()
	if size < _log_pos:
		_log_pos = 0
		_log_buf = """"
		_steam_n = 0
		_seen_ids.clear()
	if size <= _log_pos:
		return
	_last_activity_msec = Time.get_ticks_msec()  # 日志仍在增长 = 启动还活着,安全网不响
	f.seek(_log_pos)
	# get_as_text() 无视 seek、永远从头读全文件(实测),必须 get_buffer 增量
	var chunk := f.get_buffer(size - _log_pos).get_string_from_utf8()
	_log_buf += chunk
	_log_pos = size
	while true:
		var nl := _log_buf.find(""\n"")
		if nl < 0:
			break
		var line := _log_buf.substr(0, nl)
		_log_buf = _log_buf.substr(nl + 1)
		_handle_line(line)

func _handle_line(line: String) -> void:
	if ""Looking for mods to load from Steam Workshop"" in line:
		var id := _extract_item_id(line)
		if id != """" and not _seen_ids.has(id):
			_seen_ids[id] = true
			_steam_n += 1
			_ws_order.append([id, Time.get_ticks_msec()])
			var text := _txt(""bar.workshopItem"").replace(""{id}"", id)
			# 行内自带体积(size N, ...):大项更慢的解释,顺手带上
			if ""size "" in line:
				var size_str: String = line.split(""size "")[1].split("","")[0].strip_edges()
				if size_str.is_valid_int():
					text += "" · "" + String.num(float(size_str) / 1048576.0, 1) + "" MB""
			_log_line(text)
		if _steam_total < 0:
			_steam_total = _count_workshop()
		_set_progress(_steam_n, _steam_total, ""workshop "" + id)
	elif ""Found mod manifest file"" in line:
		# 工坊项内部的清单发现:给出 mod 的真实名字(比数字 id 友好)
		var p := line.substr(line.find(""Found mod manifest file"") + len(""Found mod manifest file"")).strip_edges()
		var slash: int = max(p.rfind(""/""), p.rfind(""\\""))
		var base_name := p.substr(slash + 1) if slash >= 0 else p
		var short_name: String = base_name.trim_suffix("".json"")
		_log_line(_txt(""bar.manifestFound"").replace(""{name}"", short_name))
		# 路径含 workshop/content/<appid>/<itemid>/…:记 id→名字,供扫描时序用
		var parts := p.split(""/"")
		for i in parts.size():
			if parts[i] == ""content"" and i + 2 < parts.size() and parts[i + 2].is_valid_int():
				_ws_names[parts[i + 2]] = short_name
				break
	elif ""Loading assembly DLL"" in line:
		# 首个 mod dll 开始加载 = 同步突发立即开始、帧将停止。此处绝不能再改
		# UI(见 _process 的 _frozen 注释)——mod 段的显示由 C# 条负责,只设冻结标志。
		# 标志在「本会改文字的同一迭代」内生效,竞态窗口被精确关闭。
		if _ws_end_msec == 0:
			_ws_end_msec = Time.get_ticks_msec()
		if not _frozen:
			_frozen_msec = Time.get_ticks_msec()
		_frozen = true

func _extract_item_id(line: String) -> String:
	var parts := line.split("" "")
	for i in parts.size():
		if parts[i] == ""mod"" and i + 1 < parts.size():
			return parts[i + 1]
	return """"

func _count_workshop() -> int:
	var ws_root := _workshop_root()
	if ws_root == """":
		return -1
	var dir := DirAccess.open(ws_root)
	if dir == null:
		return -1
	var n := 0
	dir.list_dir_begin()
	var name := dir.get_next()
	while name != """":
		if dir.current_is_dir() and not name.begins_with("".""):
			n += 1
		name = dir.get_next()
	dir.list_dir_end()
	return n

# ---------------- C# 桥(本节点作为唯一加载 UI) ----------------

func _stage_text(stage: int, name: String) -> String:
	return _txt(""bar.stage"").replace(""{n}"", str(stage)).replace(""{t}"", ""@@STAGE_COUNT@@"").replace(""{name}"", name)

# 活动日志推进:连续相同只记一次;帧冻结期间照常积累,恢复后可见尾部。
func _log_line(text: String) -> void:
	if text == """" or text == _last_log or _log_labels.is_empty():
		return
	_last_log = text
	_log_lines.append(text)
	if _theme == ""slaytheshin"":
		_ss_log_render()  # 3×5 整行淘汰窗口,绕开 classic/minespire 的 ACTIVITY_LINES 渲染
		return
	if _log_lines.size() > ACTIVITY_LINES:
		_log_lines = _log_lines.slice(_log_lines.size() - ACTIVITY_LINES)
	var off := _log_lines.size() - _log_labels.size()
	for i in _log_labels.size():
		_log_labels[i].text = _log_lines[i + off] if i + off >= 0 else """"

# 工坊扫描时序(C# 在 Handoff 时读取,转成瀑布图的逐项 Prelude span)。
# 首见毫秒 = 轮询观测时刻(≈该项扫描开始,0.1s 量化);相邻差分 ≈ 单项耗时。
# end = 首见 dll 加载行;0 = 未观测到(C# 用自身时刻兜底)。
# 加法式扩展:旧脚本无此方法,C# HasMethod 探测后跳过,优雅降级。
func get_workshop_log() -> Array:
	return [_ws_end_msec, _ws_order, _ws_names]

# C# 确认接管(版本协商已过)。只停工坊轮询与 shimmer,不隐藏、不替换节点;
# _process 的接管早退见上。退休仍由 takeover() 负责(隐藏图层)。
func csharp_attach() -> void:
	if not _bridge_attached:
		_bridge_attached = true
		_bridge_last_present_msec = Time.get_ticks_msec()
		print(""[LoadingBarBoot] bridge attached v"", bridge_version,
			"" @"", Engine.get_frames_drawn(), "" frames; workshop "", _steam_n, ""/"", _steam_total)

# 全程呈现(与 C# ClassicBar.Present 同一语义):
#   overall/local —— 全程条 / 当前阶段条;local<0 表示不定进度
#   stage      —— 1..7,用于阶段标题
#   step/detail —— 当前完整文案快照
# _done(已退休/被压制)或 UI 未建时静默丢弃——与 ClassicBar 的死亡标志同语义。
func csharp_present(overall: float, local: float, stage: int,
		step: String, detail: String) -> void:
	if _done or _overall_fill == null or _local_fill == null:
		return
	_bridge_last_present_msec = Time.get_ticks_msec()
	var stage_changed := stage != _last_stage
	_last_stage = stage
	_overall_target = clamp(overall, 0.0, 1.0)
	var was_indeterminate := _local_indeterminate
	_local_indeterminate = local < 0.0
	_local_target = clamp(local, 0.0, 1.0) if not _local_indeterminate else 0.0
	# 资产会话(阶段 4/5)有自然帧,可无等待地平滑批量跳变;同步阶段必须立即提交。
	_smooth_progress = not stage_changed and not was_indeterminate and not _local_indeterminate and (stage == 4 or stage == 5)
	if _local_indeterminate:
		_overall_display = _overall_target
		_overall_fill.size.x = _track_w * _overall_display
		if _theme == ""minespire"" or _theme == ""slaytheshin"":
			_local_fill.size.x = _track_w * 0.25
		else:
			_local_fill.size.x = min(@@PULSE_MIN@@, _track_w)
	elif stage_changed or not _smooth_progress:
		_overall_display = _overall_target
		_overall_fill.size.x = _track_w * _overall_display
		_local_display = _local_target
		_local_fill.size.x = _track_w * _local_display
		_local_fill.position.x = _fill_base_x
	_ss_sync_fill()  # slaytheshin:确定段/不定滑段经孪生材质可视(其余主题空表 no-op)
	_step.text = _stage_text(stage, step)
	_detail.text = detail
	# 活动日志:阶段切换记里程碑;否则记 detail——mod 的 prefix/postfix 各一行
	# (加载中 / 「id · +耗时」),资产为「n/N · 文件」,计时的裸「+ms」带上步骤名。
	if stage_changed and _theme == ""slaytheshin"":
		_ss_set_stage(stage)  # 第一排放大标记只在换阶段时重排
	if stage_changed:
		_log_line(step)
	elif detail != """":
		_log_line(step + "" "" + detail if detail.begins_with(""+"") else detail)

func takeover() -> void:
	if _done:
		return
	_done = true
	# 帧死检测:_frozen 在首个 mod dll 加载时置位且永不复位,接管后仍为 true——
	# 真正帧死的只有「冻结早退分支的 30s 安全网」(_frozen 且未接管)这一个调用点,
	# 那里 tween 永不推进,必须立即隐藏;其余路径(看门狗/菜单就绪)帧都在流动。
	var frames_alive := not _frozen or _bridge_attached
	if _fade_root != null and frames_alive:
		# minespire 揭幕淡出;classic(_fade_root 为 null)保持立即隐藏不变
		var tw := create_tween()
		tw.tween_property(_fade_root, ""modulate:a"", 0.0, @@MS_FADE_S@@)
		tw.finished.connect(_fade_finish)
	elif _layer:
		_layer.visible = false
	print(""[LoadingBarBoot] splash dismissed at frame "", Engine.get_frames_drawn())

func _fade_finish() -> void:
	if _layer:
		_layer.visible = false
";
}
