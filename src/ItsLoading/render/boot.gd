# boot.gd —— LoadingBar 启动视图 bootstrap(ItsLoading mod 自注入)。
#
# 角色:帧 0 起效的加载指示器宿主。自身不含任何主题视觉 —— 按 cfg 主题从
# user://itsloading/themes/<id>/theme.json 装载声明式主题(经 render/interpreter.gd
# 渲染),经 ctx 注入 kit 与运行环境,之后只做四件事:驱动快照(平滑数学/不定
# 相位/活动流)、桥协议端点、工坊前奏轮询、自检与自清理。
#
# 主题合同(三个动词,interpreter 实现,词汇表见 interpreter.gd 头注):
#   theme_build(ctx)   ctx = {root, viewport, mod_dir, txt, kit, theme_id, mod_version,
#                             theme_dir};返回 bool(false = 主题不可用,走回退链)
#   theme_apply(snap)  snap = {overall, local, indeterminate, t, stage, stage_changed,
#                              step, detail, log_entries}
#   theme_retire()     停自身动画;可见性(淡出/立即隐藏)归本节点
#
# 桥协议(与 C# GdBridgeBar 成对,破坏性变更双侧同步升版本):
#   csharp_attach() / csharp_present(overall, local, stage, step, detail) / csharp_set_visible() /
#   takeover() / show_hint(text) / bridge_version(实例变量)/ _done(关闭标志,C# 探测读)
#   可选方法(C# 经 HasMethod 探测,旧脚本缺席即跳过,新增不升版):
#   replay_boot_log() / get_workshop_log()
#
# C# 晚期托管:GdBridgeBar 探测失败(autoload 缺席/版本不匹配/已关闭)时,从磁盘以
# CACHE_MODE_IGNORE 重新实例化本脚本 —— 同一份主题代码,两种宿主。
# 装载一律 CACHE_MODE_IGNORE:版本过渡启动时旧实例已把旧字节载入资源缓存,
# 忽略缓存才能读到 C# Install 刚刷新的新文件。
#
# 启动时主动自检:mod 在 settings 里被禁用、或本地/工坊文件均已不存在,
# 则不显示任何进度条,并错后 2 秒做原子自清理(避开启动期 I/O;任何时刻被强退均无害)。
extends Node

const MOD_ID := "ItsLoading"
const LOG_PATH := "user://logs/godot.log"
const FRAC_END := 0.25  # 工坊段在全程刻度中的终点(与 C# BootTimeline.WorkshopEnd 配对)
const CLEANUP_DELAY := 2.0
const RENDER_DIR := "user://itsloading/render"
const KIT_PATH := "user://itsloading/render/kit.gd"
const THEME_MAP_PATH := "user://itsloading/render/theme-map.json"
const INTERPRETER_PATH := "user://itsloading/render/interpreter.gd"
const THEMES_DIR := "user://itsloading/themes"
const FALLBACK_THEME := "classic"
const STAGE_COUNT := 7  # 启动阶段数(与 C# LoadingViewState.StageCount 配对)
const FADE_S := 0.4     # 退场淡出(帧还在流动时;帧冻结时立即隐藏)
const LOG_STREAM_CAP := 60  # 活动流上限(窗口淘汰归各主题的 LogWindow)
const SMOOTH_SPEED := 5.0
const BRIDGE_WATCHDOG_MSEC := 300000  # 仅兜底 C# 中途死亡;须远高于合法的慢云同步/慢 mod 启动

var bridge_version := 12

# ---- 前奏(工坊扫描)状态 ----
var boot_start_msec := 0  # C# Handoff 读取的引擎启动锚点
var _log_pos := -1
var _log_buf := ""
var _steam_n := 0
var _seen_ids := {}
var _seen_manifests := {}   # 已发过活动行的清单路径(实时轮询与回放共用,回放幂等)
var _steam_total := -1
var _poll_acc := 0.0
var _ws_order: Array = []   # [[工坊项 id, 首见引擎毫秒], ...](日志到达序)
var _ws_names := {}         # 工坊项 id → mod 显示名(清单文件基名)
var _ws_end_msec := 0       # 扫描结束(首见 dll 加载行);0 = 未观测到
# ---- 语言/文案 ----
var _lang := "eng"
var _strings := {}
# ---- 安全网 ----
var _frozen := false
var _frozen_msec := 0         # 冻结起点:冻结分支安全网从这点起算(冷缓存工坊扫描可远超 30s)
var _last_activity_msec := 0  # 最近一次观测到 godot.log 增长的时刻:接管前安全网按「静默」计时
# ---- 桥/快照驱动 ----
var _bridge_attached := false
var _bridge_last_present_msec := 0
var _last_stage := 0
var _applied_stage := -1
var _smooth_progress := false
var _overall_display := 0.0
var _overall_target := 0.0
var _local_display := 0.0
var _local_target := 0.0
var _local_indeterminate := true
var _t := 0.0
var _elapsed := 0.0
var _step_text := ""
var _detail_text := ""
var _log_lines: Array = []  # 活动流(已去重已格式化;渲染窗口归主题)
var _activity_serial := 0    # 每次 C# 数据呈现递增；自然帧只读取，不重复触发
var _nan_count := 0         # NaN 入口哨兵计数(移除时的摘要行消费;正常恒 0)
var _last_log := ""
# ---- 自检/清理 ----
var _done := false
var _cleanup_pending := false
var _cleaned := false
# ---- 主题 ----
var _theme_id := "classic"
var _theme_node: Node
var _layer: CanvasLayer
var _root: Control  # 主题视觉的共同父节点;退场淡出对它做 modulate


func _ready() -> void:
	boot_start_msec = Time.get_ticks_msec()
	_last_activity_msec = boot_start_msec
	_detect_language()
	_theme_id = _read_theme()
	if _detect_state() != "ok":
		_done = true
		_cleanup_pending = true
		print("[LoadingBarBoot] mod disabled or unsubscribed — bar suppressed, cleanup deferred")
	else:
		_load_strings()
		if _build_ui():
			_skip_log_history()
			# 先显示 0/N,避免第一次 0.2s 日志轮询前第一眼就显示 6/N。
			_steam_total = _count_workshop()
			if _steam_total > 0:
				_set_progress(0, _steam_total, "")
			print("[LoadingBarBoot] splash ready at frame ", Engine.get_frames_drawn(),
				" (theme ", _theme_id, ")")


func _build_ui() -> bool:
	var vs: Vector2 = get_viewport().get_visible_rect().size
	_layer = CanvasLayer.new()
	# 正常路径中本层从帧 0 持续到菜单就绪;998 让晚期托管实例可叠加其上。
	_layer.layer = 998
	add_child(_layer)
	_root = Control.new()
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_layer.add_child(_root)

	var kit_script: GDScript = ResourceLoader.load(KIT_PATH, "GDScript",
		ResourceLoader.CACHE_MODE_IGNORE)
	if kit_script == null:
		push_error("[LoadingBarBoot] kit load FAILED (" + KIT_PATH + ") — no loading UI this boot")
		return false
	var theme_script := _load_theme_script(_theme_id)
	if theme_script == null and _theme_id != FALLBACK_THEME:
		# 未知主题 id(未来主题 + 旧脚本/cfg)→ 回 classic 布局,主题不因未知值失败
		print("[LoadingBarBoot] unknown theme '", _theme_id, "' — falling back to ", FALLBACK_THEME)
		_theme_id = FALLBACK_THEME
		theme_script = _load_theme_script(_theme_id)
	if theme_script == null:
		push_error("[LoadingBarBoot] theme load FAILED (", _theme_id, ") — no loading UI this boot")
		return false

	# kit 的素材根随主题来源:内置 = 本 mod themes 根;包 = 包内 themes 根
	# (主题内引用 "<id>/…" 在两种根下都成立)
	var kit = kit_script.new(_kit_root if _kit_root != "" else _mod_dir().path_join("themes"))
	if not _instantiate_theme(theme_script, kit, vs):
		# 主题构建失败(JSON 不可解析)→ classic 兜底再试一次
		_theme_node.queue_free()
		_theme_node = null
		if _theme_id != FALLBACK_THEME:
			print("[LoadingBarBoot] theme '", _theme_id, "' failed — falling back to ", FALLBACK_THEME)
			_theme_id = FALLBACK_THEME
			theme_script = _load_theme_script(_theme_id)
			if theme_script == null or not _instantiate_theme(theme_script, kit, vs):
				return false
		else:
			return false
	return true


# 实例化并构建主题(theme.gd 返回 void → null;interpreter 返回 bool。
# null != false:旧脚本永视作构建成功,失败语义只属于声明式主题)。
func _instantiate_theme(theme_script: GDScript, kit, vs: Vector2) -> bool:
	_theme_node = theme_script.new()
	_theme_node.name = "LoadingTheme"
	_root.add_child(_theme_node)
	var built = _theme_node.theme_build({
		"root": _root,
		"viewport": vs,
		"mod_dir": _mod_dir(),
		"txt": Callable(self, "_txt"),
		"kit": kit,
		"theme_id": _theme_id,
		"mod_version": _mod_version(),
		"theme_dir": _theme_dir if _theme_dir != "" else THEMES_DIR.path_join(_theme_id),
		"calib": _calib_view,
	})
	return built != false


# 主题装载:theme.json(声明式,经 interpreter.gd 渲染)。id 解析链(Inc 8):
# 镜像 themes/<id>(内置,上次启动由 C# 差异刷新)→ 缓存 theme-map.json
# (外部包,id → 包内绝对目录;C# 上次启动写入,一次性滞后是设计内行为)
# → 未知 id 回 classic。theme_dir 与 kit 素材根随解析结果切换(包主题的
# 素材在包目录里,不在镜像)。
func _load_theme_script(id: String) -> GDScript:
	var res := _resolve_theme(id)
	if res.is_empty():
		return null
	_theme_dir = res["theme_dir"]
	_kit_root = res["kit_root"]
	var interp: GDScript = ResourceLoader.load(INTERPRETER_PATH, "GDScript",
		ResourceLoader.CACHE_MODE_IGNORE)
	if interp == null:
		push_error("[LoadingBarBoot] interpreter load FAILED (" + INTERPRETER_PATH + ")")
	return interp


var _theme_dir := ""
var _kit_root := ""


func _resolve_theme(id: String) -> Dictionary:
	# 1) 内置镜像
	if FileAccess.file_exists(THEMES_DIR.path_join(id).path_join("theme.json")):
		return {"theme_dir": THEMES_DIR.path_join(id),
			"kit_root": _mod_dir().path_join("themes")}
	# 2) 外部包缓存(绝对路径;目录可能已随退订消失,读时校验)
	var map = JSON.parse_string(FileAccess.get_file_as_string(THEME_MAP_PATH))
	if map is Dictionary and map.get(id) is String:
		var dir: String = map[id]
		if dir.begins_with("/") and FileAccess.file_exists(dir.path_join("theme.json")):
			return {"theme_dir": dir, "kit_root": dir.get_base_dir()}
	return {}


# ---------------- 主题选择(与 C# ThemeRegistry 读同一 cfg) ----------------

func _read_theme() -> String:
	# 读链:独占主题文件的 Theme 键 → 旧 cfg 键(过渡启动;BaseLib 拥有该文件,
	# 属性变更后会整体重写抹掉 Theme,故只作回退)→ 旧 txt → classic。
	# 顺带读 (Debug)标定视图开关(BaseLib 的字符串布尔 "True"/"False" 也认;
	# 开关在 BaseLib 自己的 cfg 里,只从第一个路径读)。
	var p := "user://mod_configs/ItsLoading.theme.cfg"
	if FileAccess.file_exists(p):
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data is Dictionary and data.get("Theme") is String:
			return str(data["Theme"]).to_lower()
	var bl := "user://mod_configs/ItsLoading.cfg"
	if FileAccess.file_exists(bl):
		var blata = JSON.parse_string(FileAccess.get_file_as_string(bl))
		if blata is Dictionary:
			_calib_view = str(blata.get("CalibView", "False")).to_lower() == "true"
			if blata.get("Theme") is String:
				return str(blata["Theme"]).to_lower()
	var legacy := "user://itsloading_theme.txt"
	if FileAccess.file_exists(legacy):
		var s := FileAccess.get_file_as_string(legacy).strip_edges().to_lower()
		if s != "":
			return s
	return FALLBACK_THEME


var _calib_view := false



# ---------------- 快照驱动(呈现都走 _apply;主题是 snap 的纯渲染器) ----------------

func _apply() -> void:
	if _theme_node == null:
		return
	var stage_changed := _last_stage != _applied_stage
	_applied_stage = _last_stage
	_theme_node.theme_apply({
		"overall": _overall_display,
		"local": _local_display,
		"indeterminate": _local_indeterminate,
		"t": _t,
		"stage": _last_stage,
		"stage_changed": stage_changed,
		"step": _step_text,
		"detail": _detail_text,
		"log_entries": _log_lines,
		"activity_serial": _activity_serial,
	})


# 活动日志推进:连续相同只记一次;帧冻结期间照常积累,恢复后可见尾部。
func _log_line(text: String) -> void:
	if text == "" or text == _last_log:
		return
	_last_log = text
	_log_lines.append(text)
	if _log_lines.size() > LOG_STREAM_CAP:
		_log_lines = _log_lines.slice(_log_lines.size() - LOG_STREAM_CAP)


func _stage_text(stage: int, step_name: String) -> String:
	return _txt("bar.stage").replace("{n}", str(stage)).replace("{t}", str(STAGE_COUNT)).replace("{name}", step_name)


func _set_progress(n: int, total: int, detail: String) -> void:
	if total > 0 and n <= total:
		var local := float(n) / float(total)
		_overall_display = FRAC_END * local
		_overall_target = _overall_display
		_local_display = local
		_local_target = local
		_local_indeterminate = false
		_last_stage = 1
		_step_text = _stage_text(1, _txt("bar.workshop").replace("{n}", str(n)).replace("{t}", str(total)))
		_detail_text = detail
		_apply()


func _process(delta: float) -> void:
	_elapsed += delta
	if _cleanup_pending and not _cleaned and _elapsed >= CLEANUP_DELAY:
		_do_cleanup()
	if _done:
		return
	if _bridge_attached:
		# C# 正常驱动;平滑与不定相位在自然帧上推进,逐帧推快照。
		_t += delta
		if _smooth_progress:
			_overall_display = move_toward(_overall_display, _overall_target, delta * SMOOTH_SPEED)
			_local_display = move_toward(_local_display, _local_target, delta * SMOOTH_SPEED)
		_apply()
		if Time.get_ticks_msec() - _bridge_last_present_msec > BRIDGE_WATCHDOG_MSEC:
			print("[LoadingBarBoot] bridge watchdog expired — dismissing stale boot view")
			takeover()
		return
	if _frozen:
		# 同步突发已开始(首个 mod dll 加载,帧停止流动)。冻结一切 UI 变更——
		# 阻塞瞬间若存在未提交的文字变更(字形重排异步),已呈现帧会被渲染端
		# 失效 → 前奏与后续之间黑屏。60s 安全网从冻结起点起算(前置阶段合法地可超 30s)。
		if _frozen_msec > 0 and Time.get_ticks_msec() - _frozen_msec > 60000:
			takeover()
		return
	_t += delta
	_poll_acc += delta
	if _poll_acc >= 0.1:
		_poll_acc = 0.0
		_poll_log()
	_apply()
	# 接管前安全网:60s「日志静默」才关闭(C# 死亡/游戏卡死),扫描慢不算——
	# 日志还在增长就说明启动在进行(_poll_log 刷新 _last_activity_msec)。
	if Time.get_ticks_msec() - _last_activity_msec > 60000:
		print("[LoadingBarBoot] pre-bridge net: 60s log silence — dismissing")
		takeover()


# ---------------- 翻译表(mod 目录 localization/<语言>/strings.json) ----------------
# 与 C# I18n 同一张表、同一条回退链:目标语言 → eng → 键本身。

func _txt(key: String) -> String:
	# 不叫 _t:本文件已有 _t(平滑计时)字段
	return _strings.get(key, key)


func _read_strings(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}
	var data = JSON.parse_string(FileAccess.get_file_as_string(path))
	return data if data is Dictionary else {}


func _load_strings() -> void:
	var mod_dir := _mod_dir()
	if mod_dir == "":
		return
	_strings = _read_strings(mod_dir.path_join("localization/eng/strings.json"))
	if _lang != "eng":
		var overlay := _read_strings(mod_dir.path_join("localization/" + _lang + "/strings.json"))
		for k in overlay:
			_strings[k] = overlay[k]


func _detect_language() -> void:
	for p in _settings_files():
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data is Dictionary and data.get("language") is String:
			_lang = data.get("language")
			return


# ---------------- 自检与自清理 ----------------

func _detect_state() -> String:
	# 本地与工坊的 mod 文件都不在了 = 退订/移除
	if not _mod_files_present():
		return "gone"
	# 所有提到本 mod 的 settings.save 都是 disabled = 被关闭
	var mentioned := false
	var enabled_seen := false
	for p in _settings_files():
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data == null or not (data is Dictionary):
			continue
		var ms = data.get("mod_settings")
		if ms == null or not (ms is Dictionary):
			continue
		var ml = ms.get("mod_list")
		if ml == null or not (ml is Array):
			continue
		for e in ml:
			if e is Dictionary and e.get("id") == MOD_ID:
				mentioned = true
				if e.get("is_enabled", true):
					enabled_seen = true
	if mentioned and not enabled_seen:
		return "disabled"
	return "ok"


func _settings_files() -> Array:
	var out := []
	var d := DirAccess.open("user://steam")
	if d:
		d.list_dir_begin()
		var n := d.get_next()
		while n != "":
			var p := "user://steam/".path_join(n + "/settings.save")
			if d.current_is_dir() and FileAccess.file_exists(p):
				out.append(p)
			n = d.get_next()
		d.list_dir_end()
	return out


# 从可执行文件目录逐级向上探测 workshop/content/2868840:
# macOS 的 .app 布局在上方第 5 级(Contents/MacOS → …/steamapps),
# Windows/Linux 的直接布局在第 3 级(游戏目录 → …/steamapps)。
func _workshop_root() -> String:
	var d := OS.get_executable_path().get_base_dir()
	for i in range(8):
		var root := d.path_join("workshop/content/2868840")
		if DirAccess.dir_exists_absolute(root):
			return root
		var parent := d.get_base_dir()
		if parent == d:
			return ""
		d = parent
	return ""


func _mod_dir() -> String:
	# 预览注入(tools/preview_boot.sh):环境变量指定假 mod 布局;正常运行为空
	var preview := OS.get_environment("ITSLOADING_PREVIEW_MOD_DIR")
	if preview != "":
		return preview
	# 本地安装或工坊条目目录(含 ItsLoading.json 的那层);素材与翻译表都在其下
	var exe_dir := OS.get_executable_path().get_base_dir()
	var local := exe_dir.path_join("mods/" + MOD_ID)
	if FileAccess.file_exists(local.path_join(MOD_ID + ".json")):
		return local
	var ws_root := _workshop_root()
	if ws_root == "":
		return ""
	var ws := DirAccess.open(ws_root)
	if ws:
		ws.list_dir_begin()
		var n := ws.get_next()
		while n != "":
			if ws.current_is_dir() and FileAccess.file_exists(ws_root.path_join(n + "/" + MOD_ID + ".json")):
				ws.list_dir_end()
				return ws_root.path_join(n)
			n = ws.get_next()
		ws.list_dir_end()
	return ""


func _mod_files_present() -> bool:
	return _mod_dir() != ""


func _mod_version() -> String:
	# 版本号从 mod 清单读(C# 与工坊页用同一来源)
	var p := _mod_dir().path_join(MOD_ID + ".json")
	if FileAccess.file_exists(p):
		var data = JSON.parse_string(FileAccess.get_file_as_string(p))
		if data is Dictionary and data.get("version") is String:
			return data["version"]
	return ""


# 错后清理:①临时文件+rename 原子替换 override.cfg ②删我们的整树 + 旧版单文件脚本。
# 任何时刻被强退:2 秒内 = 零写入;①之后 = cfg 已干净,gd 文件惰性无害。
func _do_cleanup() -> void:
	_cleaned = true
	var exe_dir := OS.get_executable_path().get_base_dir()
	var cfg := exe_dir.path_join("override.cfg")
	if FileAccess.file_exists(cfg):
		var filtered := _cfg_without_us(FileAccess.get_file_as_string(cfg))
		var tmp := "override.cfg.lbnew"
		var w := FileAccess.open(exe_dir.path_join(tmp), FileAccess.WRITE)
		if w:
			w.store_string(filtered)
			w.close()
			var dir := DirAccess.open(exe_dir)
			if dir == null or dir.rename(tmp, "override.cfg") != OK:
				var w2 := FileAccess.open(cfg, FileAccess.WRITE)
				if w2:
					w2.store_string(filtered)
					w2.close()
	# 我们的整树(bootstrap 自身也在内 —— 已在内存,删文件无害)+ 旧版单文件脚本
	for target in ["user://itsloading", "user://loadingbar_boot.gd"]:
		var abs := ProjectSettings.globalize_path(target)
		if FileAccess.file_exists(abs) or DirAccess.dir_exists_absolute(abs):
			OS.move_to_trash(abs)
	print("[LoadingBarBoot] self-cleanup complete")


# 移除我们的标记行与 autoload 条目;若 [autoload] 段因此为空则连段头一并移除。
func _cfg_without_us(s: String) -> String:
	var pass1 := PackedStringArray()
	for line in s.split("\n"):
		var t := line.strip_edges()
		if t.begins_with(";") and "; LoadingBar mod autoload" in t:
			continue
		if "LoadingBarBoot" in t and t.find("=") > -1:
			continue
		pass1.append(line)
	var out := PackedStringArray()
	var in_autoload := false
	var autoload_empty := true
	var header_idx := -1
	for i in pass1.size():
		var t := pass1[i].strip_edges()
		if t.begins_with("["):
			if in_autoload and header_idx >= 0 and autoload_empty:
				out.remove_at(header_idx)
				header_idx = -1
			in_autoload = t.to_lower() == "[autoload]"
			if in_autoload:
				header_idx = out.size()
				autoload_empty = true
		else:
			if in_autoload and t != "" and not t.begins_with(";") and not t.begins_with("#"):
				autoload_empty = false
		out.append(pass1[i])
	if in_autoload and header_idx >= 0 and autoload_empty:
		out.remove_at(header_idx)
	return "\n".join(out)


# ---------------- 工坊前奏轮询(接管前 0 → 0.25 段的呈现) ----------------

func _skip_log_history() -> void:
	var f := FileAccess.open(LOG_PATH, FileAccess.READ)
	if f:
		_log_pos = f.get_length()


func _poll_log() -> void:
	if _log_pos < 0:
		return
	var f := FileAccess.open(LOG_PATH, FileAccess.READ)
	if f == null:
		return
	var size := f.get_length()
	if size < _log_pos:
		_log_pos = 0
		_log_buf = ""
		_steam_n = 0
		_seen_ids.clear()
	if size <= _log_pos:
		return
	_last_activity_msec = Time.get_ticks_msec()  # 日志仍在增长 = 启动还活着,安全网不响
	f.seek(_log_pos)
	# get_as_text() 无视 seek、永远从头读全文件,必须 get_buffer 增量读
	var chunk := f.get_buffer(size - _log_pos).get_string_from_utf8()
	_log_buf += chunk
	_log_pos = size
	while true:
		var nl := _log_buf.find("\n")
		if nl < 0:
			break
		var line := _log_buf.substr(0, nl)
		_log_buf = _log_buf.substr(nl + 1)
		_handle_line(line)


func _handle_line(line: String) -> void:
	if "Looking for mods to load from Steam Workshop" in line:
		var id := _extract_item_id(line)
		if id != "" and not _seen_ids.has(id):
			_seen_ids[id] = true
			_steam_n += 1
			_ws_order.append([id, Time.get_ticks_msec()])
			_log_line(_workshop_item_line(line))
		if _steam_total < 0:
			_steam_total = _count_workshop()
		_set_progress(_steam_n, _steam_total, "workshop " + id)
	elif "Found mod manifest file" in line:
		# 工坊项内部的清单发现:给出 mod 的真实名字(比数字 id 友好)
		var p := line.substr(line.find("Found mod manifest file") + len("Found mod manifest file")).strip_edges()
		if not _seen_manifests.has(p):
			_seen_manifests[p] = true
			_log_line(_txt("bar.manifestFound").replace("{name}", _manifest_short_name(p)))
		# 路径含 workshop/content/<appid>/<itemid>/…:记 id→名字,供扫描时序用
		var parts := p.split("/")
		for i in parts.size():
			if parts[i] == "content" and i + 2 < parts.size() and parts[i + 2].is_valid_int():
				_ws_names[parts[i + 2]] = _manifest_short_name(p)
				break
	elif "Loading assembly DLL" in line:
		# 首个 mod dll 开始加载 = 同步突发立即开始、帧将停止。此处绝不能再改
		# UI——mod 段的显示由 C# 时间线驱动,只设冻结标志。
		if _ws_end_msec == 0:
			_ws_end_msec = Time.get_ticks_msec()
		if not _frozen:
			_frozen_msec = Time.get_ticks_msec()
		_frozen = true


func _extract_item_id(line: String) -> String:
	var parts := line.split(" ")
	for i in parts.size():
		if parts[i] == "mod" and i + 1 < parts.size():
			return parts[i + 1]
	return ""


# 工坊项活动行文案(实时轮询与回放共用):id + 行内自带的体积
# (size N, 大项更慢时作为解释带上)
func _workshop_item_line(line: String) -> String:
	var text := _txt("bar.workshopItem").replace("{id}", _extract_item_id(line))
	if "size " in line:
		var size_str: String = line.split("size ")[1].split(",")[0].strip_edges()
		if size_str.is_valid_int():
			text += " · " + String.num(float(size_str) / 1048576.0, 1) + " MB"
	return text


func _manifest_short_name(path: String) -> String:
	var slash: int = max(path.rfind("/"), path.rfind("\\"))
	var base_name := path.substr(slash + 1) if slash >= 0 else path
	return base_name.trim_suffix(".json")


# ---------------- 前奏活动行回放(C# attach 时调用;可选协议方法) ----------------

# 工坊扫描期主循环不迭代(帧在 C# 之后才流动)的启动形态下,_process 轮询
# 零观测,前奏活动日志为空;C# 桥在场后由此方法从本次运行日志头补齐显示。
# 幂等:与实时轮询共用 _seen_ids/_seen_manifests 去重,正常启动时这些行已
# 发过 → no-op。只进 _log_lines —— 不计数、不推进度、不记工坊时序
# (回放的观测时刻已失真,未计时段落在瀑布图按聚合显示)。
func replay_boot_log() -> void:
	if _done:
		return
	var f := FileAccess.open(LOG_PATH, FileAccess.READ)
	if f == null:
		return
	var text := f.get_buffer(f.get_length()).get_string_from_utf8()
	for line in text.split("\n"):
		_replay_line(line)


func _replay_line(line: String) -> void:
	if "Looking for mods to load from Steam Workshop" in line:
		var id := _extract_item_id(line)
		if id != "" and not _seen_ids.has(id):
			_seen_ids[id] = true
			_log_line(_workshop_item_line(line))
	elif "Found mod manifest file" in line:
		var p := line.substr(line.find("Found mod manifest file") + len("Found mod manifest file")).strip_edges()
		if not _seen_manifests.has(p):
			_seen_manifests[p] = true
			_log_line(_txt("bar.manifestFound").replace("{name}", _manifest_short_name(p)))


func _count_workshop() -> int:
	var ws_root := _workshop_root()
	if ws_root == "":
		return -1
	var dir := DirAccess.open(ws_root)
	if dir == null:
		return -1
	var n := 0
	dir.list_dir_begin()
	var name := dir.get_next()
	while name != "":
		if dir.current_is_dir() and not name.begins_with("."):
			n += 1
		name = dir.get_next()
	dir.list_dir_end()
	return n


# 工坊扫描时序(C# 在 Handoff 时读取,转成瀑布图的逐项 Prelude span)。
# 首见毫秒 = 轮询观测时刻(≈该项扫描开始,0.1s 量化);相邻差分 ≈ 单项耗时。
# end = 首见 dll 加载行;0 = 未观测到(C# 用自身时刻兜底)。
# 旧脚本无此方法,C# 经 HasMethod 探测,缺席则跳过。
func get_workshop_log() -> Array:
	return [_ws_end_msec, _ws_order, _ws_names, _log_lines.duplicate()]


# ---------------- C# 桥(本节点作为唯一加载 UI) ----------------

# C# 确认接管(版本协商已过)。只停工坊轮询,不隐藏、不替换节点。
func csharp_attach() -> void:
	if not _bridge_attached:
		_bridge_attached = true
		_bridge_last_present_msec = Time.get_ticks_msec()
		print("[LoadingBarBoot] bridge attached v", bridge_version,
			" @", Engine.get_frames_drawn(), " frames; workshop ", _steam_n, "/", _steam_total)


# 首次注入提示(C# 侧 BootSplash.InjectedThisRun 时经 HasMethod 探测调用)
func show_hint(text: String) -> void:
	if _done or _root == null:
		return
	var l := Label.new()
	l.text = text
	l.position = Vector2(24.0, 24.0)
	l.add_theme_font_size_override("font_size", 14)
	l.add_theme_color_override("font_color", Color(0.2, 0.85, 0.9, 1.0))
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.add_child(l)
	var t := get_tree().create_timer(8.0)
	t.timeout.connect(func() -> void: l.queue_free())


# 全程呈现(v12):step_text 已含阶段包装、log 为全量流(含前奏行)——
# 两者由 C# 侧 LoadingPresentation 统一产出,本侧只做平滑与渲染。
# log 非空才替换(空 = C# 侧检测未变,沿用上次);_done/主题未建时静默丢弃。
func csharp_present(overall: float, local: float, stage: int,
		step_text: String, detail_text: String, log) -> void:
	if _done or _theme_node == null:
		return
	_bridge_last_present_msec = Time.get_ticks_msec()
	_activity_serial += 1
	# NaN 入口哨兵:clampf 拦不住 NaN,NaN 进原生布局会让渲染错乱;
	# 报错一次 + 计数,移除时的摘要行消费
	if is_nan(overall) or is_nan(local):
		_nan_count += 1
		push_error("[ItsLoading] NaN present #%d: overall=%s local=%s stage=%d step='%s'" %
			[_nan_count, overall, local, stage, step_text])
		if is_nan(local):
			local = -1.0
		if is_nan(overall):
			overall = 0.0
	var stage_changed := stage != _last_stage
	_last_stage = stage
	_overall_target = clampf(overall, 0.0, 1.0)
	var was_indeterminate := _local_indeterminate
	_local_indeterminate = local < 0.0
	_local_target = clampf(local, 0.0, 1.0) if not _local_indeterminate else 0.0
	# 资产会话(阶段 4/5)有自然帧,可无等待地平滑批量跳变;同步阶段必须立即提交。
	_smooth_progress = not stage_changed and not was_indeterminate and not _local_indeterminate and (stage == 4 or stage == 5)
	if _local_indeterminate or stage_changed or not _smooth_progress:
		_overall_display = _overall_target
		if not _local_indeterminate:
			_local_display = _local_target
	_step_text = step_text
	_detail_text = detail_text
	if log is Array and log.size() > 0:
		_log_lines = log
	_apply()


# native 接管时只隐藏、不销毁，保留为同一主题与 LoadingFrame 的 standby。
func csharp_set_visible(visible: bool) -> void:
	if _layer != null:
		_layer.visible = visible


func takeover() -> void:
	if _done:
		return
	_done = true
	if _theme_node != null and _theme_node.has_method("theme_retire"):
		_theme_node.theme_retire()
	# 帧是否流动:_frozen 在首个 mod dll 加载时置位且永不复位,接管后仍为 true——
	# 真正冻结的只有「冻结分支安全网」这一个调用点,那里 tween 永不推进,
	# 必须立即隐藏;其余路径(看门狗/菜单就绪)帧都在流动。
	var frames_alive := not _frozen or _bridge_attached
	if _root != null and frames_alive:
		var tw := create_tween()
		tw.tween_property(_root, "modulate:a", 0.0, FADE_S)
		tw.finished.connect(_fade_finish)
	elif _layer:
		_layer.visible = false
	print("[LoadingBarBoot] splash dismissed at frame ", Engine.get_frames_drawn())


func _fade_finish() -> void:
	if _layer:
		_layer.visible = false
