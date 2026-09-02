# interpreter.gd —— theme.json 声明式主题的通用渲染器(boot.gd 装载,与
# theme.gd 同一三动词契约;theme.gd 已退役,本脚本是唯一主题实现)。
#
# 单一事实源:同一份 theme.json 既驱动本渲染器(阶段 0/2 的 Godot 树),
# 也将驱动原生冻结呈现面(MacLayerSurface)—— 主题视觉只声明一次。
#
# 词汇表 v1(format:1;构建门禁 tools/check_themes.py 闭环校验):
#   space: {"kind":"screen"} 或 {"kind":"design","w":854,"h":480}
#   elements[]:{id, type, parent?, …}—— z 序 = 数组序,parent 引用先出现的容器
#     bg          {color}
#     logo        {src, x, y, w, fallback_text, fallback_font, fallback_color, nearest}
#     strip       {h}                     底部条带容器(PRESET_BOTTOM_WIDE)
#     label       {text(字符串|{"loc":键}), bind?, x, y, w?, h?, font, color, align?, overrun?}
#     version_label {prefix, x, y, w?, h?, font, color, align?, overrun?}
#     bar_solid   {x, y, w("fill"|px), h, track, fill, bind, indeterminate?}
#     bar_outline {x, y, w, h, border_w, inset, border, fill, bind, indeterminate?}
#     icon_row    {count, size, gap, cx, cy|bottom, pivot?, src|pattern+index_base,
#                  nearest?, placeholder?, enlarge?{factor}}(enlarge = 绑 stage)
#     dots        {of: <icon_row id>, scale, color, cy}
#     mask_track  {members: [<icon_row/dots id>…], tint, bind:"local", indeterminate}
#                 (轨分数域 = 首个 icon_row 成员的行几何;副本 tint 规则:
#                  icon 成员 = tint, dots 成员 = dots.color × tint)
#     sprite      {src, x, y, w, h, frame_w, frame_h, frames, fps, nearest, activity?}
#                 (activity:{frames_per_update}按数据活动额外推进;基础 fps 始终自主播放)
#     log_column  {x, y, lines, line_h, font, color, bind:"log"}
#     log_rows    {x, y, w, lines, per_line, sep, line_h, font, color, align?, overrun?}
#   bind ∈ {overall, local, stage, step, detail, log, version}
#   颜色一律 #RRGGBBAA;长度/字体 = 主题空间单位(screen 即像素,design 经 DesignSpace)
#   w:"fill" = 所在空间宽度 − 2x;align/overrun 传 Godot 常量整数值
# 失败策略:逐元素 —— 未知类型/未知 bind/父/成员引用缺失 → 跳过该元素 + push_error
# (一个拼写错误不再整主题回退);整体 JSON 不可解析 → theme_build 返回 false,
# boot.gd 走回退链(→ classic → 无加载 UI)。
# 观测面:inspect() → {bars, labels, logs, rows, geoms, mask}
# (preview_driver / check_theme_geometry 的确定性读数只走这里,不翻私有字段)。
extends Node

var _kit
var _d                   # kit.DesignSpace;null = screen 空间
var _space_w := 0.0      # 所在空间的宽度("fill" 的基准)
var _theme_id := ""
var _bars := {}          # id -> kit.BarFill
var _bar_binds := {}     # id -> String(overall|local)
var _labels := {}        # id -> Label(step/detail 绑定)
var _label_binds := {}   # id -> String(step|detail)
var _logs := {}          # id -> kit.LogWindow
var _indeterminate := {} # id -> {mode:"pulse",min_w,travel} | {mode:"slide",cycle_s}
var _rows := {}          # id -> Array[Control](icon_row)
var _row_geoms := {}     # id -> Dictionary(icon_row 的行几何)
var _enlarge := {}       # id -> float(绑定 stage 的放大倍数)
var _dot_rects := {}     # id -> Array[Rect2]
var _dot_colors := {}    # id -> Color(蒙版副本 tint 用)
var _mask                # kit.MaskFill
var _mask_ind := {}      # mask_track 的 indeterminate 参数(apply 用)
var _sprites := {}       # id -> kit.SpriteAnimator(自主时钟)
var _sprite_activity := {} # id -> 每次数据更新额外推进帧数
var _last_activity_serial := 0
var _retired := false
var _ok := false


func theme_build(ctx: Dictionary) -> bool:
	_kit = ctx.kit
	_theme_id = str(ctx.theme_id)
	var path: String = ctx.theme_dir.path_join("theme.json")
	var data = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not (data is Dictionary):
		push_error("[theme] %s 解析失败(非 JSON 对象)— 主题不可用" % path)
		return false
	if int(data.get("format", 0)) != 1:
		push_error("[theme] %s format=%s ≠ 1 — 主题不可用" % [path, str(data.get("format"))])
		return false

	var space: Dictionary = data.get("space", {"kind": "screen"})
	if str(space.get("kind", "screen")) == "design":
		var dw := float(space.get("w", 854.0))
		_d = _kit.design(ctx.viewport, dw, float(space.get("h", 480.0)))
		_space_w = _d.s(dw)
	else:
		_d = null
		_space_w = ctx.viewport.x

	var parents := {"": ctx.root}
	for e in data.get("elements", []):
		if not (e is Dictionary):
			push_error("[theme] 元素非对象 — 跳过")
			continue
		_build_element(e, ctx, parents)
	if bool(ctx.get("calib", false)) and _d != null:
		_calib_grid(ctx, data)
		_calib_boxes(ctx, data.get("elements", []))
	_ok = true
	return true


# ---------------------------------------------------------------- (Debug)标定视图
# 与 C# 侧 CalibRules.cs 同一套估计规则(单一事实源在那边,改两处必须同步):框来自
# theme.json 声明值(与视觉字形无关)—— 双渲染器截图程序化比对布局。

func _calib_rect(e: Dictionary) -> Array:
	var t := str(e.get("type", ""))
	var x: float = float(e.get("x", 0.0))
	var y: float = float(e.get("y", 0.0))
	match t:
		"label", "version_label":
			var f: float = float(e.get("font", 14.0))
			return [x, y, float(e.get("w", 240.0)), float(e.get("h", f * 1.4 + 6.0))]
		"bar_solid", "bar_outline":
			var w2: float
			if str(e.get("w", "")) == "fill":
				w2 = float(_space_w) / _d.s(1.0) - 2.0 * x
			else:
				w2 = float(e.get("w", 0.0))
			return [x, y, w2, float(e.get("h", 5.0))]
		"logo":
			var w3: float = float(e.get("w", 100.0))
			return [x, y, w3, w3 * 0.3]
		"icon_row":
			var count := int(e.get("count", 1))
			var size: float = float(e.get("size", 32.0))
			var gap: float = float(e.get("gap", 0.0))
			var span: float = count * size + (count - 1.0) * gap
			var top: float = float(e.get("bottom", float(e.get("cy", 0.0)) + size / 2.0)) - size
			return [float(e.get("cx", 0.0)) - span / 2.0, top, span, size]
		"dots", "mask_track":
			return []  # 由其引用的 icon_row 框覆盖(_calib_boxes 特判)
		"sprite":
			return [x, y, float(e.get("w", 0.0)), float(e.get("h", 0.0))]
		"log_column":
			return [x, y, 240.0, float(e.get("lines", 10)) * float(e.get("line_h", 17.0))]
		"log_rows":
			return [x, y, float(e.get("w", 0.0)), float(e.get("lines", 3)) * float(e.get("line_h", 12.0))]
	return []


func _calib_boxes(ctx: Dictionary, elements: Array) -> void:
	var by_id := {}
	for e in elements:
		if e is Dictionary and str(e.get("type", "")) == "icon_row":
			by_id[str(e.get("id", ""))] = e
	for e in elements:
		if not (e is Dictionary):
			continue
		var r: Array
		if str(e.get("type", "")) == "mask_track":
			for m in e.get("members", []):
				if by_id.has(str(m)):
					r = _calib_rect(by_id[str(m)])
					break
		else:
			r = _calib_rect(e)
		if r.is_empty():
			continue
		var p: Vector2 = _pt(r[0], r[1])
		_calib_box(ctx.root, Rect2(p, Vector2(_sc(r[2]), _sc(r[3]))))


func _calib_box(root: Control, rect: Rect2) -> void:
	var c := Color(1, 0, 1, 0.85)
	var w := 2.0
	for side in [
		Rect2(rect.position, Vector2(rect.size.x, w)),
		Rect2(rect.position + Vector2(0, rect.size.y - w), Vector2(rect.size.x, w)),
		Rect2(rect.position, Vector2(w, rect.size.y)),
		Rect2(rect.position + Vector2(rect.size.x - w, 0), Vector2(w, rect.size.y)),
	]:
		var l := ColorRect.new()
		l.position = side.position
		l.size = side.size
		l.color = c
		l.mouse_filter = Control.MOUSE_FILTER_IGNORE
		root.add_child(l)


func _calib_grid(ctx: Dictionary, data: Dictionary) -> void:
	var space: Dictionary = data.get("space", {})
	var w := float(space.get("w", 854.0))
	var h := float(space.get("h", 480.0))
	for i in range(1, 10):
		var strong := i == 5
		var col := Color(1, 1, 1, 0.7) if strong else Color(1, 1, 1, 0.3)
		var t := i / 10.0
		var lw := 4.0 if strong else 2.0
		var v := ColorRect.new()
		v.position = _pt(w * t, 0.0)
		v.size = Vector2(lw, ctx.viewport.y)
		v.color = col
		v.mouse_filter = Control.MOUSE_FILTER_IGNORE
		ctx.root.add_child(v)
		var hl := ColorRect.new()
		hl.position = _pt(0.0, h * t)
		hl.size = Vector2(ctx.viewport.x, lw)
		hl.color = col
		hl.mouse_filter = Control.MOUSE_FILTER_IGNORE
		ctx.root.add_child(hl)



func theme_apply(snap: Dictionary) -> void:
	if not _ok or _retired:
		return
	if bool(snap.stage_changed):
		for id_ in _enlarge:
			_kit.mark_stage(_rows[id_], int(snap.stage), _enlarge[id_])
	if _mask != null:
		_mask.segment(float(snap.local), bool(snap.indeterminate), float(snap.t),
			float(_mask_ind.get("cycle_s", 3.0)))
	for id_ in _bars:
		match str(_bar_binds[id_]):
			"overall":
				_bars[id_].set_fraction(float(snap.overall))
			"local":
				if bool(snap.indeterminate) and _indeterminate.has(id_):
					var m: Dictionary = _indeterminate[id_]
					if str(m.get("mode", "slide")) == "pulse":
						_bars[id_].set_width_px(
							_kit.pulse_width(float(snap.t), float(m.get("min_w", 60.0)),
								float(m.get("travel", 160.0))))
					else:
						_bars[id_].slide(_kit.slide_phase(float(snap.t), float(m.get("cycle_s", 3.0))))
				else:
					_bars[id_].set_fraction(float(snap.local))
	for id_ in _labels:
		_labels[id_].text = str(snap.get(str(_label_binds[id_]), ""))
	for id_ in _logs:
		_logs[id_].render(snap.log_entries)
	var activity_serial := int(snap.get("activity_serial", 0))
	if activity_serial > _last_activity_serial:
		var updates := activity_serial - _last_activity_serial
		for id_ in _sprite_activity:
			_sprites[id_].advance(float(_sprite_activity[id_]) * updates)
	_last_activity_serial = activity_serial


func theme_retire() -> void:
	_retired = true
	for sprite in _sprites.values():
		sprite.stopped = true


# ---------------------------------------------------------------- 观测面(工具只读这里)

func inspect() -> Dictionary:
	return {"bars": _bars, "labels": _labels, "logs": _logs,
		"rows": _rows, "geoms": _row_geoms, "mask": _mask, "enlarge": _enlarge,
		"sprites": _sprites, "sprite_activity": _sprite_activity}


# ---------------------------------------------------------------- 元素构建

func _build_element(e: Dictionary, ctx: Dictionary, parents: Dictionary) -> void:
	var id_: String = str(e.get("id", ""))
	var t: String = str(e.get("type", ""))
	if id_ == "":
		push_error("[theme] 元素 type=%s 缺 id — 跳过" % t)
		return
	var parent: Control = parents.get(str(e.get("parent", "")))
	if parent == null:
		push_error("[theme] %s(id=%s)父引用 '%s' 未先出现 — 跳过" % [t, id_, str(e.get("parent"))])
		return
	match t:
		"bg":
			_kit.bg(parent, {"color": _color(e.get("color", "#00000000"))})
		"logo":
			var p: Vector2 = _pt(float(e.get("x", 0.0)), float(e.get("y", 0.0)))
			_kit.logo(parent, {
				"file": _asset(e.get("src", ""), ctx),
				"x": p.x, "y": p.y, "w": _sc(float(e.get("w", 100.0))),
				"fallback_text": str(e.get("fallback_text", "")),
				"fallback_font": _font(float(e.get("fallback_font", 28.0))),
				"fallback_color": _color(e.get("fallback_color", "#ffffffff")),
				"nearest": bool(e.get("nearest", true))})
		"strip":
			var strip := Control.new()
			strip.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
			strip.offset_top = -float(e.get("h", 76.0))
			ctx.root.add_child(strip)
			parents[id_] = strip
		"label":
			var bind: String = str(e.get("bind", "step"))
			if not _bind_known(bind):
				return
			_labels[id_] = _kit.label(parent, _label_style(e, ctx))
			_label_binds[id_] = bind
		"version_label":
			# 版本号 = prefix + mod 版本(构建期一次成型,无绑定更新)
			var ve := e.duplicate()
			ve["text"] = str(e.get("prefix", "")) + str(ctx.mod_version)
			_kit.label(parent, _label_style(ve, ctx))
		"bar_solid":
			_build_bar(id_, e, parent, true)
		"bar_outline":
			_build_bar(id_, e, parent, false)
		"icon_row":
			var size: float = float(e.get("size", 32.0))
			var st := {
				"count": int(e.get("count", 1)), "size": _sc(size),
				"gap": _sc(float(e.get("gap", 0.0))),
				"cx": _pt(float(e.get("cx", 0.0)), 0.0).x,
				"nearest": bool(e.get("nearest", true)),
			}
			if e.has("cy"):
				st["cy"] = _pt(0.0, float(e["cy"])).y
			if e.has("bottom"):
				st["bottom"] = _pt(0.0, float(e["bottom"])).y
			if str(e.get("pivot", "")) != "":
				st["pivot"] = str(e["pivot"])
			if e.has("src"):
				st["file"] = _asset(e["src"], ctx)
			else:
				st["file_pattern"] = _asset(str(e.get("pattern", "%d")), ctx)
				st["index_base"] = int(e.get("index_base", 1))
			if e.has("placeholder"):
				st["placeholder_color"] = _color(e["placeholder"])
			var r: Dictionary = _kit.icon_row(parent, st)
			_rows[id_] = r.row
			_row_geoms[id_] = r.geom
			# 副本素材引用(mask_track 用):贴图在节点上,矩形按节点当前位置
			if e.has("enlarge"):
				var f: float = float(e.get("enlarge", {}).get("factor", 1.0))
				_enlarge[id_] = f
				_kit.mark_stage(_rows[id_], 1, f)  # 工坊期(阶段 1)起即有放大标记
		"dots":
			var of: String = str(e.get("of", ""))
			if not _row_geoms.has(of):
				push_error("[theme] dots(id=%s)引用的行 '%s' 未先出现 — 跳过" % [id_, of])
				return
			_dot_rects[id_] = _kit.gap_dots(parent, _row_geoms[of], {
				"dot_scale": float(e.get("scale", 0.2)),
				"color": _color(e.get("color", "#ffffffff")),
				"cy": _pt(0.0, float(e.get("cy", 0.0))).y})
			_dot_colors[id_] = _color(e.get("color", "#ffffffff"))
		"mask_track":
			var members: Array = []
			var domain: Dictionary = {}
			for mid in e.get("members", []):
				var m := str(mid)
				if _rows.has(m):
					if domain.is_empty():
						domain = _row_geoms[m]
					for c in _rows[m]:
						members.append({"tex": (c.texture if c is TextureRect else null),
							"rect": Rect2(c.position, c.size), "tint": null})
				elif _dot_rects.has(m):
					for r2 in _dot_rects[m]:
						members.append({"tex": _kit.circle_tex(), "rect": r2,
							"tint": _dot_colors[m] * _color(e.get("tint", "#808080ff"))})
				else:
					push_error("[theme] mask_track(id=%s)成员 '%s' 未先出现 — 忽略该成员" % [id_, m])
			if domain.is_empty():
				push_error("[theme] mask_track(id=%s)没有可用的行成员 — 跳过" % id_)
				return
			var tint := _color(e.get("tint", "#808080ff"))
			for m in members:
				if m["tint"] == null:
					m["tint"] = tint  # icon 成员:贴图自身灰阶 × tint
			_mask = _kit.mask_track(parent, domain, members)
			_mask_ind = e.get("indeterminate", {})
		"sprite":
			var p3: Vector2 = _pt(float(e.get("x", 0.0)), float(e.get("y", 0.0)))
			var animator = _kit.sprite(parent, {
				"file": _asset(e.get("src", ""), ctx),
				"rect": Rect2(p3, Vector2(_sc(float(e.get("w", 0.0))), _sc(float(e.get("h", 0.0))))),
				"frame_w": float(e.get("frame_w", 0.0)), "frame_h": float(e.get("frame_h", 0.0)),
				"frames": int(e.get("frames", 1)), "fps": float(e.get("fps", 12.0)),
				"nearest": bool(e.get("nearest", true))})
			if animator != null:
				_sprites[id_] = animator
				var activity = e.get("activity", {})
				if activity is Dictionary and activity.has("frames_per_update"):
					_sprite_activity[id_] = float(activity["frames_per_update"])
		"log_column":
			var p4: Vector2 = _pt(float(e.get("x", 0.0)), float(e.get("y", 0.0)))
			_logs[id_] = _kit.log_column(parent, {
				"x": p4.x, "y": p4.y, "lines": int(e.get("lines", 10)),
				"line_h": _sc(float(e.get("line_h", 17.0))),
				"font": _font(float(e.get("font", 12.0))),
				"color": _color(e.get("color", "#ffffffff"))})
		"log_rows":
			var p5: Vector2 = _pt(float(e.get("x", 0.0)), float(e.get("y", 0.0)))
			var st5 := {
				"x": p5.x, "y": p5.y, "w": _sc(float(e.get("w", 0.0))),
				"lines": int(e.get("lines", 3)), "per_line": int(e.get("per_line", 5)),
				"sep": str(e.get("sep", " | ")), "line_h": _sc(float(e.get("line_h", 12.0))),
				"font": _font(float(e.get("font", 12.0))),
				"color": _color(e.get("color", "#ffffffff"))}
			if e.has("align"):
				st5["align"] = int(e["align"])
			if e.has("overrun"):
				st5["overrun"] = int(e["overrun"])
			_logs[id_] = _kit.log_rows(parent, st5)
		_:
			push_error("[theme] 未知元素类型 '%s'(id=%s)— 跳过" % [t, id_])


# bar_solid / bar_outline 的公共构建(bind 校验 + 几何解析 + 进度登记)
func _build_bar(id_: String, e: Dictionary, parent: Control, solid: bool) -> void:
	var bind: String = str(e.get("bind", ""))
	if not _bind_known(bind):
		return
	var x: float = float(e.get("x", 0.0))
	var y: float = float(e.get("y", 0.0))
	var w: float = _sc(float(e.get("w", 0.0))) if str(e.get("w", "")) != "fill" \
		else _space_w - _sc(x) * 2.0
	var p: Vector2 = _pt(x, y)
	var st := {"x": p.x, "y": p.y, "w": w, "h": _sc(float(e.get("h", 5.0)))}
	if solid:
		st["track_color"] = _color(e.get("track", "#ffffffff"))
		st["fill_color"] = _color(e.get("fill", "#ffffffff"))
		_bars[id_] = _kit.bar_solid(parent, st)
	else:
		st["border_w"] = _sc(float(e.get("border_w", 2.0)))
		st["inset"] = _sc(float(e.get("inset", 4.0)))
		st["border_color"] = _color(e.get("border", "#ffffffff"))
		st["fill_color"] = _color(e.get("fill", "#ffffffff"))
		_bars[id_] = _kit.bar_outline(parent, st)
	_bar_binds[id_] = bind
	if e.has("indeterminate"):
		_indeterminate[id_] = e["indeterminate"]


func _label_style(e: Dictionary, ctx: Dictionary) -> Dictionary:
	var p: Vector2 = _pt(float(e.get("x", 0.0)), float(e.get("y", 0.0)))
	var st := {
		"text": _text(e.get("text", ""), ctx),
		"x": p.x, "y": p.y,
		"font": _font(float(e.get("font", 14.0))),
		"color": _color(e.get("color", "#ffffffff")),
	}
	if e.has("w"):
		st["w"] = _sc(float(e["w"]))
		st["h"] = _sc(float(e.get("h", 0.0)))
	if e.has("align"):
		st["align"] = int(e["align"])
	if e.has("overrun"):
		st["overrun"] = int(e["overrun"])
	return st


func _bind_known(b: String) -> bool:
	if b in ["overall", "local", "step", "detail", "log"]:
		return true
	push_error("[theme] 未知 bind '%s' — 元素跳过" % b)
	return false


# ---------------------------------------------------------------- 空间/取值解析

func _pt(x: float, y: float) -> Vector2:
	return _d.p(x, y) if _d != null else Vector2(x, y)


func _sc(v: float) -> float:
	return _d.s(v) if _d != null else v


func _font(f: float) -> int:
	return _d.font(f) if _d != null else maxi(1, roundi(f))


func _color(v, fallback := Color.WHITE) -> Color:
	var s := str(v)
	return Color.html(s) if Color.html_is_valid(s) else fallback


# 素材路径:theme.json 内相对主题目录,kit 的素材根是 themes/ 树 —— 加 <id>/ 前缀。
# 路径防逃逸(外部主题是不可信数据):拒绝绝对路径与 ".."(词汇表封闭之外的
# 唯一文件系统触点)
func _asset(src, ctx: Dictionary) -> String:
	var s := str(src)
	if s.begins_with("/") or s.begins_with("res://") or s.begins_with("user://") \
			or "/../" in s or s.begins_with("../"):
		push_error("[theme] 非法素材路径 '%s' — 跳过" % s)
		return ""
	return _theme_id + "/" + s


func _text(v, ctx: Dictionary) -> String:
	if v is Dictionary and v.has("loc"):
		return ctx.txt.call(str(v["loc"]))
	return str(v)
