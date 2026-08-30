# preview_driver.gd —— 离屏预览驱动(preview_boot.sh 复制为 preview.gd)。
#
# 职责:把假 mod 布局里的 gd 树镜像到 user://itsloading(复刻 C# Install 的
# 同步)→ 逐主题:写 cfg(走真实 _read_theme 读链)→ 实例化 boot.gd →
# 时间线剧本(csharp_attach / csharp_present 各阶段 / 平滑 / 不定 / takeover
# 淡出)→ 截图 + 确定性读数。
#
# 读数 = 回归面:fill/track 百分比、pos.x(滞留即 bug)、gachathespire 的放大表与
# 蒙版段;同时全树做越轨断言,任何 FAIL 退出码 1。截图看观感,读数做回归。
extends Node

const THEMES := ["classic", "minespire", "gachathespire"]

var boot: Node
var _fails := 0


func _ready() -> void:
	var mod_dir := OS.get_environment("ITSLOADING_PREVIEW_MOD_DIR")
	if mod_dir == "":
		push_error("[preview] 缺 ITSLOADING_PREVIEW_MOD_DIR(preview_boot.sh 负责)")
		get_tree().quit(1)
		return
	var user_dir := ProjectSettings.globalize_path("user://itsloading")
	_rm_tree(user_dir)
	DirAccess.make_dir_recursive_absolute(user_dir)
	_copy_tree(mod_dir.path_join("gd"), user_dir)
	for theme in THEMES:
		await _run_theme(theme)
	print("[preview] summary: fails=", _fails, " (", THEMES.size(), " themes)")
	get_tree().quit(1 if _fails > 0 else 0)


# ---------------------------------------------------------------- 时间线剧本

func _run_theme(id: String) -> void:
	print("[preview] ==== theme ", id, " ====")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("user://mod_configs"))
	var f := FileAccess.open("user://mod_configs/ItsLoading.cfg", FileAccess.WRITE)
	f.store_string("{\n  \"Theme\": \"%s\"\n}\n" % id)
	f.close()
	var shots := OS.get_environment("PREVIEW_SHOTS").path_join(id)
	DirAccess.make_dir_recursive_absolute(shots)

	var script: GDScript = ResourceLoader.load("user://itsloading/boot.gd", "GDScript",
		ResourceLoader.CACHE_MODE_IGNORE)
	boot = script.new()
	# root 忙:直接 add_child 会被拒,deferred 进树
	get_tree().root.add_child.call_deferred(boot)
	await get_tree().create_timer(1.0).timeout
	_snap(shots, "01_boot_ready_stage1")
	_readback(id, "stage1")
	boot.csharp_attach()
	boot.csharp_present(0.30, 0.40, 2, "Loading mods", "ItsLoading.dll +12ms")
	await get_tree().create_timer(0.3).timeout
	_snap(shots, "02_stage2_local40")
	_readback(id, "stage2")
	boot.csharp_present(0.60, -1.0, 3, "Essential data", "")
	await get_tree().create_timer(0.6).timeout
	_snap(shots, "03_stage3_indeterminate")
	_readback(id, "stage3")
	boot.csharp_present(0.70, 0.10, 4, "Opening assets", "12/120 splash.png")
	boot.csharp_present(0.72, 0.60, 4, "Opening assets", "72/120 title.png")
	await get_tree().create_timer(0.9).timeout
	_snap(shots, "04_stage4_smooth")
	_readback(id, "stage4")
	for i in range(20):
		boot.csharp_present(0.80, float(i) / 20.0, 5, "Menu assets", "item_%d.png +3ms" % i)
		await get_tree().create_timer(0.05).timeout
	_snap(shots, "05_stage5_log_full")
	_readback(id, "stage5")
	boot.csharp_present(1.0, 1.0, 7, "Main menu", "ready")
	await get_tree().create_timer(0.3).timeout
	_snap(shots, "06_stage7_done")
	boot.takeover()
	await get_tree().create_timer(0.2).timeout
	_snap(shots, "07_fade_mid")
	await get_tree().create_timer(1.0).timeout
	boot.queue_free()
	await get_tree().create_timer(0.3).timeout


# ---------------------------------------------------------------- 读数/断言/截图

func _readback(id: String, tag: String) -> void:
	var t = boot.get("_theme_node")
	if t != null and id == "gachathespire":
		var row: Array = t.get("_row1")
		var scales := []
		for i in row.size():
			scales.append(String.num(row[i].scale.x, 2))
		var mats: Array = t.get("_mask").get("_mats")
		var seg := -1.0
		if not mats.is_empty():
			seg = float(mats[0].get_shader_parameter("seg_b"))
		print("[readback] ", id, "/", tag, " scale=", scales,
			" mask=", String.num(seg, 3))
	elif t != null:
		var local = t.get("_local")
		var fill: ColorRect = local.get("_fill")
		print("[readback] ", id, "/", tag,
			" fill/track=", String.num(fill.scale.x * 100.0, 1), "%",
			" pos.x=", String.num(fill.position.x, 1),
			" rect.w=", String.num(fill.size.x, 1))
	var layer = boot.get("_layer")
	if layer != null:
		_check_contains(layer, "%s/%s" % [id, tag])


# 全树越轨断言:条填充(Panel/ColorRect 轨的 ColorRect 子节点)恒在轨内。
func _check_contains(node: Node, where: String) -> void:
	for c in node.get_children():
		if c is ColorRect and (node is Panel or node is ColorRect):
			var right: float = c.position.x + c.size.x * absf(c.scale.x)
			if c.position.x < -0.01 or right > node.size.x + 0.01:
				_fails += 1
				push_error("[geom] %s 填充越轨: pos.x=%s 右缘=%s 轨宽=%s" %
					[where, c.position.x, right, node.size.x])
		_check_contains(c, where)


func _snap(shots: String, name: String) -> void:
	var img := get_viewport().get_texture().get_image()
	img.save_png(shots.path_join(name + ".png"))
	print("[preview] snap ", name)


# ---------------------------------------------------------------- 树操作

func _rm_tree(abs_dir: String) -> void:
	var d := DirAccess.open(abs_dir)
	if d == null:
		return
	d.list_dir_begin()
	var names: Array[String] = []
	var n := d.get_next()
	while n != "":
		if n != "." and n != "..":
			names.append(n)
		n = d.get_next()
	d.list_dir_end()
	for name in names:
		var full := abs_dir.path_join(name)
		if DirAccess.dir_exists_absolute(full):
			_rm_tree(full)
		else:
			DirAccess.remove_absolute(full)
	DirAccess.remove_absolute(abs_dir)


func _copy_tree(src_abs: String, dst_abs: String) -> void:
	DirAccess.make_dir_recursive_absolute(dst_abs)
	var d := DirAccess.open(src_abs)
	if d == null:
		push_error("[preview] 源目录打不开: " + src_abs)
		return
	d.list_dir_begin()
	var n := d.get_next()
	while n != "":
		if n != "." and n != "..":
			if d.current_is_dir():
				_copy_tree(src_abs.path_join(n), dst_abs.path_join(n))
			else:
				DirAccess.copy_absolute(src_abs.path_join(n), dst_abs.path_join(n))
		n = d.get_next()
	d.list_dir_end()
