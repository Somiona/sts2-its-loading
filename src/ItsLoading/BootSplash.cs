using System.IO;
using System.Linq;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace ItsLoading;

// ---------------------------------------------------------------- 启动画面(gd splash)自注入
//
// 架构拆分 #2 从 ItsLoading.cs 原样搬出:帧 0 起效的 GDScript 底部条
// (覆盖 C# 加载前的 0→0.25 段)与其安装/交接/延迟回收。
// gd↔C# 字符串契约(AutoloadName · "boot_start_msec" · "takeover")集中在本类。

internal static class BootSplash
{
    private const string AutoloadName = "LoadingBarBoot";
    private const string GdUserPath = "user://loadingbar_boot.gd";
    private const string CfgMarker = "; LoadingBar mod autoload";

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
            // 引擎启动锚点(gd frame 0)+ 前奏阶段(引擎启动+工坊读取)时长
            Variant anchor = boot.Get("boot_start_msec");
            if (anchor.VariantType == Variant.Type.Int)
            {
                Recorder.BootAnchorMsec = anchor.AsInt64();
                long nowMsec = (long)Time.GetTicksMsec();
                if (Recorder.BootAnchorMsec >= 0)
                {
                    Recorder.PhaseSpans.Add(new Api.LoadSpan(
                        "phase.prelude", Api.LoadPhase.Prelude,
                        Recorder.BootAnchorMsec, nowMsec - Recorder.BootAnchorMsec, ""));
                    Recorder.PhaseSpans.Add(new Api.LoadSpan(
                        "phase.engine_init", Api.LoadPhase.Prelude,
                        0, Recorder.BootAnchorMsec, ""));
                }
            }
            // 不隐藏 gd splash!让 C# 条(层级 999)直接叠在 gd 条(层级 998)上面。
            // 隐藏 CanvasLayer 会触发渲染状态变更,在无新帧提交时可能被 MoltenVK 清屏 —— 这就是黑屏间隙的来源。
            // gd 的 _process(shimmer)继续跑但不被看见;延迟到 C# 条移除时一并清理。
            _bootSplashNode = boot;
            Log.Warn("[ItsLoading] boot splash kept visible under mod bar (no takeover)");
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
    /// 颜色 token(@@*_COLOR@@)由 BuildBootSplashGd() 用共享常量替换——勿在模板里写死颜色;
    /// 几何常量(24/8/20/36/14/56/5/64/48)与 C# BuildBar 成对,改布局需手动同步。
    /// </summary>
    private static readonly string BootSplashGd = BuildBootSplashGd();

    private static string BuildBootSplashGd() =>
        BootSplashGdTemplate
            .Replace("@@TRACK_COLOR@@", GdColor(ItsLoading.BarTrackColor))
            .Replace("@@DETAIL_COLOR@@", GdColor(ItsLoading.BarDetailColor))
            .Replace("@@FILL_COLOR@@", GdColor(ItsLoading.BarFillColor));

    /// <summary>Color → GDScript 字面量(不变文化,防区域设置把小数点变逗号)。</summary>
    private static string GdColor(Color c) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Color({c.R:0.####}, {c.G:0.####}, {c.B:0.####}, {c.A:0.####})");

    private const string BootSplashGdTemplate = @"extends Node
# LoadingBar boot splash — injected by ItsLoading mod. BOOT_VERSION = 7
# 启动时主动自检:mod 在 settings 里被禁用、或本地/工坊文件均已不存在,
# 则不显示任何进度条,并错后 2 秒做原子自清理(避开启动期 I/O;任何时刻被强退均无害)。
# 正常路径:与 C# 侧一致的底部条(无垫底),负责进度刻度 0 → 0.25,
# 尾部增量跟踪 godot.log 显示工坊读取进度,C# 初始化后 takeover() 接管。

const LOG_PATH := ""user://logs/godot.log""
const FRAC_END := 0.25
const MOD_ID := ""ItsLoading""
const CLEANUP_DELAY := 2.0

var _layer: CanvasLayer
var _step: Label
var _detail: Label
var _fill: ColorRect
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
var _lang_zh := false
var _frozen := false

func _ready() -> void:
	boot_start_msec = Time.get_ticks_msec()
	_detect_language()
	if _detect_state() != ""ok"":
		_done = true
		_cleanup_pending = true
		print(""[LoadingBarBoot] mod disabled or unsubscribed — bar suppressed, cleanup deferred"")
	else:
		_build_ui()
		_skip_log_history()
		print(""[LoadingBarBoot] splash ready at frame "", Engine.get_frames_drawn())

func _detect_language() -> void:
	for p in _settings_files():
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data is Dictionary and data.get(""language"") is String:
			_lang_zh = data.get(""language"") == ""zhs""
			return

func txt(zh: String, en: String) -> String:
	return zh if _lang_zh else en

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

func _mod_files_present() -> bool:
	var exe_dir := OS.get_executable_path().get_base_dir()
	if FileAccess.file_exists(exe_dir.path_join(""mods/"" + MOD_ID + ""/"" + MOD_ID + "".json"")):
		return true
	var ws_root := _workshop_root()
	if ws_root == """":
		return false
	var ws := DirAccess.open(ws_root)
	if ws:
		ws.list_dir_begin()
		var n := ws.get_next()
		while n != """":
			if ws.current_is_dir() and FileAccess.file_exists(ws_root.path_join(n + ""/"" + MOD_ID + "".json"")):
				ws.list_dir_end()
				return true
			n = ws.get_next()
		ws.list_dir_end()
	return false

# 错后清理:①临时文件+rename 原子替换 override.cfg ②删脚本 ③删遗留心跳文件。
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
	for f in [""user://loadingbar_boot.gd"", ""user://loadingbar.heartbeat""]:
		if FileAccess.file_exists(f):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(f))
	print(""[LoadingBarBoot] self-cleanup complete"")

# 移除我们的标记行与 autoload 条目;若 [autoload] 段因此为空则连段头一并移除。
func _cfg_without_us(s: String) -> String:
	var pass1 := PackedStringArray()
	for line in s.split(""\n""):
		var t := line.strip_edges()
		if t.begins_with("";"") and ""LoadingBar mod autoload"" in t:
			continue
		if ""LoadingBarBoot"" in t and t.find(""="") > -1:
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
	# 998:C# 条在 999。两条同为 999 时,首次 ForceDraw 的同层双 canvas 会让
	# MoltenVK 掉帧 → prelude 与 C# 条之间出现 ~0.4s 黑屏(2026-08-26 实测)。
	_layer.layer = 998
	add_child(_layer)

	var strip := Control.new()
	strip.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	strip.offset_top = -64.0
	_layer.add_child(strip)

	_step = Label.new()
	_step.position = Vector2(24, 8)
	_step.add_theme_font_size_override(""font_size"", 20)
	_step.add_theme_color_override(""font_color"", Color.WHITE)
	_step.text = txt(""正在启动"", ""Starting"")
	strip.add_child(_step)

	_detail = Label.new()
	_detail.position = Vector2(24, 36)
	_detail.add_theme_font_size_override(""font_size"", 14)
	_detail.add_theme_color_override(""font_color"", @@DETAIL_COLOR@@)
	_detail.text = ""engine boot""
	strip.add_child(_detail)

	_track_w = vs.x - 48.0
	var track := ColorRect.new()
	track.position = Vector2(24, 56)
	track.size = Vector2(_track_w, 5)
	track.color = @@TRACK_COLOR@@
	strip.add_child(track)

	_fill = ColorRect.new()
	_fill.position = Vector2.ZERO
	_fill.size = Vector2(0, 5)
	_fill.color = @@FILL_COLOR@@
	track.add_child(_fill)

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
	if _frozen:
		# 同步突发已开始(首个 mod dll 加载,帧停止流动):冻结一切 UI 变更。
		# 阻塞瞬间若存在未提交的文字变更(字形重排异步),已呈现帧会被渲染端
		# 失效 → prelude 与 C# 条之间黑屏(2026-08-26 实测间歇复现)。30s 安全网仍生效。
		if _elapsed > 30.0:
			takeover()
		return
	_t += delta
	_poll_acc += delta
	if _poll_acc >= 0.2:
		_poll_acc = 0.0
		_poll_log()
	if _steam_total <= 0:
		var w: float = 60.0 + abs(fmod(_t * 0.8, 2.0) - 1.0) * 160.0
		_fill.size.x = min(w, _track_w)
	if _elapsed > 30.0:
		takeover()

func _set_progress(n: int, total: int, detail: String) -> void:
	if total > 0 and n <= total:
		var frac := FRAC_END * float(n) / float(total)
		_fill.size.x = _track_w * frac
		_step.text = txt(""创意工坊读取 %d/%d"", ""Reading Workshop %d/%d"") % [n, total]
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
		if _steam_total < 0:
			_steam_total = _count_workshop()
		_set_progress(_steam_n, _steam_total, ""workshop "" + id)
	elif ""Loading assembly DLL"" in line:
		# 首个 mod dll 开始加载 = 同步突发立即开始、帧将停止。此处绝不能再改
		# UI(见 _process 的 _frozen 注释)——mod 段的显示由 C# 条负责,只设冻结标志。
		# 标志在「本会改文字的同一迭代」内生效,竞态窗口被精确关闭。
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

func takeover() -> void:
	if _done:
		return
	_done = true
	if _layer:
		_layer.visible = false
	print(""[LoadingBarBoot] splash dismissed at frame "", Engine.get_frames_drawn())
";
}
