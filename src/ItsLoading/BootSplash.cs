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
    ///   @@*_COLOR@@ / @@*_Y@@ / @@*_HEIGHT@@ —— ClassicBar 的样式常量,
    ///   gd 正常路径与 C# 首启兜底共享同一真源。
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
            .Replace("@@PULSE_TRAVEL@@", GdFloat(ClassicBar.IndeterminateTravel));

    /// <summary>Color → GDScript 字面量(不变文化,防区域设置把小数点变逗号)。</summary>
    private static string GdColor(Color c) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Color({c.R:0.####}, {c.G:0.####}, {c.B:0.####}, {c.A:0.####})");

    private static string GdFloat(float value) => value.ToString(
        "0.####", System.Globalization.CultureInfo.InvariantCulture);

    private const string BootSplashGdTemplate = @"extends Node
# LoadingBar boot view — injected by ItsLoading mod. BOOT_VERSION = 16
# 启动时主动自检:mod 在 settings 里被禁用、或本地/工坊文件均已不存在,
# 则不显示任何进度条,并错后 2 秒做原子自清理(避开启动期 I/O;任何时刻被强退均无害)。
# 正常路径:与 C# 侧一致的底部条(无垫底),负责进度刻度 0 → 0.25,
# 尾部增量跟踪 godot.log 显示工坊读取进度。
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

func _ready() -> void:
	boot_start_msec = Time.get_ticks_msec()
	_detect_language()
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
	# 不叫 _t:与 shimmer 计时字段 var _t 冲突会让整个脚本解析失败(2026-08-28 实测)
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
# 固定走 5 级在 Win/Linux 会高出 Steam 库两级,工坊检测永远失败(todo#3)。
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
	# C# ClassicBar(999)可以无闪烁覆盖它。
	_layer.layer = 998
	add_child(_layer)

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
	if _done:
		return
	if _bridge_attached:
		# C# 正常驱动两条;无可测局部总量时只在自然帧上跑轻量 pulse。
		_t += delta
		if _local_indeterminate:
			var w: float = @@PULSE_MIN@@ + abs(fmod(_t * 0.8, 2.0) - 1.0) * @@PULSE_TRAVEL@@
			_local_fill.size.x = min(w, _track_w)
		elif _smooth_progress:
			_overall_display = move_toward(_overall_display, _overall_target, delta * SMOOTH_SPEED)
			_local_display = move_toward(_local_display, _local_target, delta * SMOOTH_SPEED)
			_overall_fill.size.x = _track_w * _overall_display
			_local_fill.size.x = _track_w * _local_display
		if Time.get_ticks_msec() - _bridge_last_present_msec > BRIDGE_WATCHDOG_MSEC:
			print(""[LoadingBarBoot] bridge watchdog expired — dismissing stale boot view"")
			takeover()
		return
	if _frozen:
		# 同步突发已开始(首个 mod dll 加载,帧停止流动):冻结一切 UI 变更。
		# 阻塞瞬间若存在未提交的文字变更(字形重排异步),已呈现帧会被渲染端
		# 失效 → prelude 与 C# 条之间黑屏(2026-08-26 实测间歇复现)。30s 安全网仍生效。
		if _elapsed > 30.0:
			takeover()
		return
	_t += delta
	_poll_acc += delta
	if _poll_acc >= 0.1:
		_poll_acc = 0.0
		_poll_log()
	if _steam_total <= 0:
		var w: float = @@PULSE_MIN@@ + abs(fmod(_t * 0.8, 2.0) - 1.0) * @@PULSE_TRAVEL@@
		_local_fill.size.x = min(w, _track_w)
	if _elapsed > 30.0:
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
		_local_fill.size.x = min(@@PULSE_MIN@@, _track_w)
	elif stage_changed or not _smooth_progress:
		_overall_display = _overall_target
		_overall_fill.size.x = _track_w * _overall_display
		_local_display = _local_target
		_local_fill.size.x = _track_w * _local_display
	_step.text = _stage_text(stage, step)
	_detail.text = detail
	# 活动日志:阶段切换记里程碑;否则记 detail——mod 的 prefix/postfix 各一行
	# (加载中 / 「id · +耗时」),资产为「n/N · 文件」,计时的裸「+ms」带上步骤名。
	if stage_changed:
		_log_line(step)
	elif detail != """":
		_log_line(step + "" "" + detail if detail.begins_with(""+"") else detail)

func takeover() -> void:
	if _done:
		return
	_done = true
	if _layer:
		_layer.visible = false
	print(""[LoadingBarBoot] splash dismissed at frame "", Engine.get_frames_drawn())
";
}
