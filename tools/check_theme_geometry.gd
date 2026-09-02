# check_theme_geometry.gd —— 主题几何不变量门禁(headless,毫秒级)。
#
# 把「填充恒在轨内」这类不变量固化成断言,防止控件实现改动破坏约束;
# 接入构建门禁(check_gd_template.py 调用)。
#
# 断言面:
#   1. kit 裸控件:滑段右缘恰好达轨满;滑段滞留后 set_fraction 必须归位左缘
#   2. 每主题整树:不定↔可测来回切 + 换阶段矩阵下,所有条填充恒在轨内
#   3. gachathespire:放大标记唯一且矩形不变;蒙版段参数在轨分数域内
#
# 用法:godot --headless -s tools/check_theme_geometry.gd   (cwd = 仓库根)
# 失败:打印 GEOM FAIL 并 OS.set_exit_code(1)
extends MainLoop

const RENDER_DIR := "res://src/ItsLoading/render"
const THEMES_DIR := "res://src/ItsLoading/themes"
const VIEWPORT := Vector2(1733.0, 975.0)  # 与实机同量级,暴露缩放类问题

var _fails := 0


func _initialize() -> void:
	var kit: GDScript = load(RENDER_DIR + "/kit.gd")

	_check_bare_bars(kit)
	var names := _theme_names()
	if names.is_empty():
		_fail("主题目录没有 theme.json:" + THEMES_DIR)
	for theme in names:
		_check_theme(kit, theme)

	if _fails == 0:
		print("  几何不变量(kit 裸控件 + %d 主题矩阵): 通过" % names.size())
	else:
		push_error("GEOM FAIL: %d 处不变量被破坏" % _fails)
		OS.set_exit_code(1)


# 主题目录扫描(新增主题 = 建 themes/<id>/theme.json,无需改本文件)
func _theme_names() -> Array[String]:
	var out: Array[String] = []
	var d := DirAccess.open(THEMES_DIR)
	if d == null:
		return out
	d.list_dir_begin()
	var n := d.get_next()
	while n != "":
		if d.current_is_dir() and not n.begins_with(".") \
				and FileAccess.file_exists(THEMES_DIR + "/" + n + "/theme.json"):
			out.append(n)
		n = d.get_next()
	d.list_dir_end()
	out.sort()
	return out


func _process(_delta: float) -> bool:
	return true


func _fail(msg: String) -> void:
	_fails += 1
	push_error("GEOM FAIL: " + msg)


# ---------------------------------------------------------------- kit 裸控件

func _check_bare_bars(kit: GDScript) -> void:
	var k = kit.new("")
	var host := Control.new()  # 孤儿节点即可:几何是数据,不依赖在树
	var outline = k.bar_outline(host, {"x": 100.0, "y": 100.0, "w": 812.0, "h": 40.0,
		"border_w": 8.0, "inset": 8.0, "border_color": Color.WHITE, "fill_color": Color.WHITE})
	var panel: Panel = host.get_child(0)
	var fill: ColorRect = panel.get_child(0)
	var track: float = 812.0 - 2.0 * 8.0

	# 滑段右缘恰好达轨满(inset+track = 右内缘);右缘用有效宽 = size×scale
	outline.slide(1.0)
	var reach: float = fill.position.x + fill.size.x * absf(fill.scale.x)
	if absf(reach - (8.0 + track)) > 0.5:
		_fail("bar_outline.slide(1.0) 右缘 %s ≠ 轨满 %s(行程系数被吃掉或双重乘)" %
			[reach, 8.0 + track])

	# 滑段滞留后切确定进度:必须归位左缘且不越界(滑段 bug 的直接复现)
	outline.slide(0.8)
	outline.set_fraction(0.7)
	if fill.position.x > 8.0 + 0.01:
		_fail("slide→set_fraction 后 pos.x=%s 未归位左缘 %s" % [fill.position.x, 8.0])
	if fill.position.x + fill.size.x * absf(fill.scale.x) > 8.0 + track + 0.01:
		_fail("slide→set_fraction 后右缘 %s 越出轨内右缘 %s" %
			[fill.position.x + fill.size.x * absf(fill.scale.x), 8.0 + track])
	if absf(fill.size.x - track) > 0.01:
		_fail("填充矩形被改动(size.x=%s ≠ 满轨 %s):变换驱动的矩形必须终身不变" %
			[fill.size.x, track])

	# 实心条同款(经典主题)
	var host2 := Control.new()
	var solid = k.bar_solid(host2, {"x": 0.0, "y": 0.0, "w": 500.0, "h": 5.0,
		"track_color": Color.WHITE, "fill_color": Color.WHITE})
	var sfill: ColorRect = host2.get_child(0).get_child(0)
	solid.slide(1.0)
	solid.set_fraction(0.9)
	if sfill.position.x > 0.01 or sfill.position.x + sfill.size.x * absf(sfill.scale.x) > 500.01:
		_fail("bar_solid slide→set_fraction 越界: pos=%s 有效宽=%s" %
			[sfill.position.x, sfill.size.x * absf(sfill.scale.x)])


# ---------------------------------------------------------------- 整主题矩阵

# 主题装载:theme.json(声明式)—— 与 boot.gd 同一装载语义,经 interpreter.gd
func _load_theme_script(id: String) -> GDScript:
	return load(RENDER_DIR + "/interpreter.gd")


func _check_theme(kit: GDScript, id: String) -> void:
	var theme_script: GDScript = _load_theme_script(id)
	var theme = theme_script.new()
	var root := Control.new()
	theme.theme_build({
		"root": root, "viewport": VIEWPORT, "mod_dir": "",
		"txt": Callable(self, "_txt"), "kit": kit.new(""),
		"theme_id": id, "mod_version": "check",
		"theme_dir": THEMES_DIR + "/" + id,
	})

	# 矩阵:不定(t 各相位)→ 可测(各分数)交替 + 换阶段,每步后查全树条几何
	var snaps: Array = []
	for t in [0.0, 0.9, 1.7, 2.9]:
		snaps.append({"indeterminate": true, "t": float(t), "local": 0.0})
	for f in [0.0, 0.35, 0.7, 1.0]:
		snaps.append({"indeterminate": false, "t": 1.0, "local": float(f)})
	snaps.append({"indeterminate": true, "t": 1.5, "local": 0.0})  # 再切回不定
	snaps.append({"indeterminate": false, "t": 1.5, "local": 0.5})

	for stage in range(1, 8):
		for i in snaps.size():
			var s: Dictionary = snaps[i]
			theme.theme_apply({
				"overall": 0.5, "local": s.local, "indeterminate": s.indeterminate,
				"t": s.t, "stage": stage, "stage_changed": i == 0,
				"step": "s", "detail": "d", "log_entries": ["a", "b", "c"],
			})
			_check_fill_containment(root, "%s stage%d snap%d" % [id, stage, i])

	# 声明式主题特有(经 inspect() 观测面):行标记唯一且矩形不变、蒙版段在轨分数域
	var el: Dictionary = theme.inspect()
	for row_id in el["enlarge"]:
		_check_row_markers(str(row_id), el["rows"][row_id], el["geoms"][row_id])
	if el["mask"] != null:
		_check_mask_domain(el["mask"])
	theme.theme_retire()  # 冒烟:retire 后 apply 静默
	theme.theme_apply({"overall": 1.0, "local": 1.0, "indeterminate": false,
		"t": 0.0, "stage": 7, "stage_changed": false, "step": "", "detail": "",
		"log_entries": []})


# 全树不变量:每个"父是 Panel/ColorRect 轨"的 ColorRect 填充恒在父矩形内。
func _check_fill_containment(node: Node, where: String) -> void:
	for c in node.get_children():
		if c is ColorRect and node is Panel:
			var right: float = c.position.x + c.size.x * absf(c.scale.x)
			if c.position.x < -0.01 or right > node.size.x + 0.01:
				_fail("%s: 填充越出轨 pos.x=%s 右缘=%s 轨宽=%s" %
					[where, c.position.x, right, node.size.x])
		_check_fill_containment(c, where)


# 行标记不变量(绑定 stage 放大的 icon_row):控件数 = 声明 count、放大标记唯一、
# 底边不动(矩形终身不变)。
func _check_row_markers(row_id: String, row: Array, geom: Dictionary) -> void:
	if row.size() != int(geom.count):
		_fail("%s 行控件数 %d ≠ 声明 count %d" % [row_id, row.size(), int(geom.count)])
		return
	var enlarged := 0
	var bottom: float = row[0].position.y + row[0].size.y
	for i in row.size():
		if row[i].scale.x > 1.01:
			enlarged += 1
		# 矩形终身不变:底边(= 不动点)构建后不应被动过 —— 与构建值一致
		if absf(row[i].position.y + row[i].size.y - bottom) > 0.01:
			_fail("%s 行底缘不一致(矩形被动过): %d" % [row_id, i])
	if enlarged != 1:
		_fail("%s 行放大标记数 %d ≠ 1" % [row_id, enlarged])


# 蒙版段不变量:段参数在轨分数域 [0, 1] 内。
func _check_mask_domain(mask) -> void:
	var mats: Array = mask.get("_mats")
	if mats.is_empty():
		_fail("蒙版材质为空")
		return
	var seg_a: float = mats[0].get_shader_parameter("seg_a")
	var seg_b: float = mats[0].get_shader_parameter("seg_b")
	if seg_a < -0.01 or seg_b > 1.01 or seg_b < seg_a - 0.01:
		_fail("蒙版段 [%s, %s] 越出轨分数域 [0, 1]" % [seg_a, seg_b])


func _txt(key: String) -> String:
	return key
