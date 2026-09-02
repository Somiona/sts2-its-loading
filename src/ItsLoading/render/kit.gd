# kit.gd —— 主题控件库(boot.gd 装载,经 ctx.kit 注入主题)。
#
# 约束内嵌在控件实现里 —— 主题作者能碰的只有样式字典。
# 内嵌约束(作者无需知道;改控件实现时必须保持):
#   · 不用 Container、直接 add_child(同步突发期 Container 布局不执行)
#   · 所有控件 MOUSE_FILTER_IGNORE(整屏覆盖期不响应输入)
#   · TextureRect 的 expand_mode 必须先于 texture 赋值(KEEP_SIZE 最小尺寸钳制陷阱)
#   · 进度写入只走各控件验证过的安全路径(BarFill 的 scale/position 变换 / icon 的 Scale / shader 参数)
# 样式 = 工厂调用点的字典,值是原生类型(Color/float/Vector2),写错就报错;
# 未知键 _check 会 push_error —— preview 每轮必跑,等价编译期检查。
# 只有 ≥2 个主题共用的机制才进本库;单一消费者留在主题文件里。
extends RefCounted

var _mod_dir := ""


func _init(mod_dir := "") -> void:
	_mod_dir = mod_dir


# ================================================================ 坐标

# 设计矩形(如 854×480)→ 屏幕的等比缩放居中;classic 用屏幕锚定,不用它。
class DesignSpace extends RefCounted:
	var scale := 1.0
	var origin := Vector2.ZERO

	func _init(viewport: Vector2, w: float, h: float) -> void:
		scale = minf(viewport.x / w, viewport.y / h)
		origin = (viewport - Vector2(w, h) * scale) * 0.5

	func p(x: float, y: float) -> Vector2:
		return origin + Vector2(x, y) * scale

	func s(v: float) -> float:
		return v * scale

	func font(f: float) -> int:
		return maxi(1, roundi(f * scale))

	func center_rect(cx: float, cy: float, size: float) -> Rect2:
		return Rect2(p(cx - size * 0.5, cy - size * 0.5), Vector2(s(size), s(size)))

	# 底边中点锚:放大时底边不动、原地向上/两侧长大(只写变换,突发冻结期照常生效)
	func bottom_rect(cx: float, bottom: float, size: float) -> Rect2:
		return Rect2(p(cx - size * 0.5, bottom - size), Vector2(s(size), s(size)))


func design(viewport: Vector2, w: float, h: float) -> DesignSpace:
	return DesignSpace.new(viewport, w, h)


# ================================================================ 控件工厂

# 整屏底色
func bg(parent: Control, st: Dictionary) -> ColorRect:
	_check("bg", st, ["color"])
	var c := ColorRect.new()
	c.color = st.get("color", Color.BLACK)
	c.set_anchors_preset(Control.PRESET_FULL_RECT)
	c.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(c)
	return c


# 文字标签。align/overrun 传 Godot 常量(如 HORIZONTAL_ALIGNMENT_CENTER)。
func label(parent: Control, st: Dictionary) -> Label:
	_check("label", st, ["text", "x", "y", "w", "h", "font", "color", "align", "overrun"])
	var l := Label.new()
	l.text = str(st.get("text", ""))
	l.position = Vector2(st.get("x", 0.0), st.get("y", 0.0))
	if st.has("w"):
		l.size = Vector2(st.get("w", 0.0), st.get("h", 0.0))
	l.add_theme_font_size_override("font_size", int(st.get("font", 14)))
	l.add_theme_color_override("font_color", st.get("color", Color.WHITE))
	if st.has("align"):
		l.horizontal_alignment = st.get("align")
	if st.has("overrun"):
		l.text_overrun_behavior = st.get("overrun")
	l.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(l)
	return l


# logo slot:高按图比例自适应;缺图回退同位居中文字标题 —— 主题不因缺图失败。
# 坐标一律屏幕坐标(设计矩形先经 DesignSpace 换算)。
func logo(parent: Control, st: Dictionary) -> Control:
	_check("logo", st, ["file", "x", "y", "w", "fallback_text", "fallback_font", "fallback_color", "nearest"])
	var x: float = st.get("x", 0.0)
	var y: float = st.get("y", 0.0)
	var w: float = st.get("w", 100.0)
	var fallback_font := int(st.get("fallback_font", 28))
	var tex := load_texture(str(st.get("file", "")))
	if tex == null:
		return label(parent, {
			"text": st.get("fallback_text", ""),
			"x": x,
			"y": y,
			"w": w,
			"h": float(fallback_font) + 6.0,
			"font": fallback_font,
			"color": st.get("fallback_color", Color.WHITE),
			"align": HORIZONTAL_ALIGNMENT_CENTER,
		})
	var h: float = w * tex.get_height() / maxf(1.0, tex.get_width())
	var t := TextureRect.new()
	t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:必须先于 texture
	t.texture = tex
	t.position = Vector2(x, y)
	t.size = Vector2(w, h)
	t.stretch_mode = TextureRect.STRETCH_SCALE
	if st.get("nearest", true):
		t.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
	t.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(t)
	return t


# icon slot:缺图 → 灰方块占位(布局数学不变)。pivot "bottom" = 底边中点锚 ——
# 返回的 Control 用 Scale 放大当前阶段(只写变换路径,永不改矩形)。
func icon(parent: Control, st: Dictionary) -> Control:
	_check("icon", st, ["file", "rect", "placeholder_color", "pivot", "nearest"])
	var rect: Rect2 = st.get("rect", Rect2())
	var c := _tex_or_placeholder(parent, str(st.get("file", "")), rect,
		st.get("placeholder_color", Color.GRAY), st.get("nearest", true))
	_apply_pivot(c, rect, str(st.get("pivot", "center")))
	return c


# 直接持有贴图的图元(生成纹理如圆点)。icon 的无文件版。
func tex_rect(parent: Control, st: Dictionary) -> Control:
	_check("tex_rect", st, ["tex", "rect", "modulate", "pivot", "nearest"])
	var rect: Rect2 = st.get("rect", Rect2())
	var t := TextureRect.new()
	t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:必须先于 texture
	t.texture = st.get("tex")
	t.position = rect.position
	t.size = rect.size
	t.stretch_mode = TextureRect.STRETCH_SCALE
	if st.has("modulate"):
		t.modulate = st.get("modulate")
	if st.get("nearest", true):
		t.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
	t.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(t)
	_apply_pivot(t, rect, str(st.get("pivot", "center")))
	return t


func _apply_pivot(c: Control, rect: Rect2, pivot: String) -> void:
	match pivot:
		"bottom":
			c.pivot_offset = Vector2(rect.size.x * 0.5, rect.size.y)
		_:
			c.pivot_offset = rect.size * 0.5


func _tex_or_placeholder(parent: Control, file: String, rect: Rect2,
		placeholder: Color, nearest: bool) -> Control:
	var tex := load_texture(file)
	var c: Control
	if tex != null:
		var t := TextureRect.new()
		t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:必须先于 texture
		t.texture = tex
		t.stretch_mode = TextureRect.STRETCH_SCALE
		if nearest:
			t.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
		c = t
	else:
		var ph := ColorRect.new()
		ph.color = placeholder
		c = ph
	c.position = rect.position
	c.size = rect.size
	c.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(c)
	return c


# ================================================================ 进度条
# 两个工厂各自只有一种正确用法;进度只经 BarFill 写入(变换驱动,见 _init 注释)。

class BarFill extends RefCounted:
	var _fill: ColorRect
	var _track_w := 0.0
	var _base_x := 0.0

	func _init(fill: ColorRect, track_w: float, base_x: float) -> void:
		_fill = fill
		_track_w = maxf(1.0, track_w)
		_base_x = base_x
		# 进度只走变换路径:矩形终身满轨、构建期一次写死(构建写入不经过
		# 突发冻结);之后 scale/position 冻结期照常生效,而 size 写入会被
		# ForceDraw 丢弃、滞留旧值(滑段后条整体右偏越框;图标放大同理,
		# 用 pivot+scale)。pivot=左缘,scale.x 即填充分数。
		_fill.pivot_offset = Vector2.ZERO
		_fill.size.x = _track_w
		_fill.scale.x = 0.0

	# 进度入口(NaN 钳 0,静默);「矩形终身不变/恒在轨内」由变换驱动结构性
	# 保证,几何门禁(check_theme_geometry.gd)负责断言,运行时不重复防
	func set_fraction(f: float) -> void:
		_fill.scale.x = clampf(0.0 if is_nan(f) else f, 0.0, 1.0)
		_fill.position.x = _base_x

	func set_width_px(w: float) -> void:
		_fill.scale.x = clampf((0.0 if is_nan(w) else w) / _track_w, 0.0, 1.0)
		_fill.position.x = _base_x

	# 不定滑段:1/4 宽从左到右扫(phase = slide_phase 的行程分数 0→1)
	func slide(phase: float, width_frac := 0.25, travel_frac := 0.75) -> void:
		var ph := 0.0 if is_nan(phase) else clampf(phase, 0.0, 1.0)
		_fill.scale.x = width_frac
		_fill.position.x = _base_x + ph * _track_w * travel_frac


# 实心条(classic):{x,y,w,h,track_color,fill_color}
func bar_solid(parent: Control, st: Dictionary) -> BarFill:
	_check("bar_solid", st, ["x", "y", "w", "h", "track_color", "fill_color"])
	var track := ColorRect.new()
	track.position = Vector2(st.get("x", 0.0), st.get("y", 0.0))
	track.size = Vector2(st.get("w", 0.0), st.get("h", 0.0))
	track.color = st.get("track_color", Color.WHITE)
	track.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(track)
	var fill := ColorRect.new()
	fill.position = Vector2.ZERO
	fill.size = Vector2(0.0, st.get("h", 0.0))
	fill.color = st.get("fill_color", Color.WHITE)
	fill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	track.add_child(fill)
	return BarFill.new(fill, st.get("w", 0.0), 0.0)


# 描边条(minespire 的 nine-slice 像素复刻):
# {x,y,w,h,border_w,inset,border_color,fill_color}
# 填充契约:净宽 = w - 2*inset,滑段基线 = inset(fill 是 outline 的子节点)。
func bar_outline(parent: Control, st: Dictionary) -> BarFill:
	_check("bar_outline", st, ["x", "y", "w", "h", "border_w", "inset", "border_color", "fill_color"])
	var border_w: float = st.get("border_w", 2.0)
	var inset: float = st.get("inset", 4.0)
	var w: float = st.get("w", 0.0)
	var h: float = st.get("h", 0.0)
	var sb := StyleBoxFlat.new()
	sb.border_color = st.get("border_color", Color.WHITE)
	sb.draw_center = false
	sb.set_border_width_all(roundi(border_w))
	var outline := Panel.new()
	outline.position = Vector2(st.get("x", 0.0), st.get("y", 0.0))
	outline.size = Vector2(w, h)
	outline.mouse_filter = Control.MOUSE_FILTER_IGNORE
	outline.add_theme_stylebox_override("panel", sb)
	parent.add_child(outline)
	var fill := ColorRect.new()
	fill.position = Vector2(inset, inset)
	fill.size = Vector2(0.0, h - 2.0 * inset)
	fill.color = st.get("fill_color", Color.WHITE)
	fill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	outline.add_child(fill)
	return BarFill.new(fill, w - 2.0 * inset, inset)


# ================================================================ 图标行 / 蒙版轨道
# (gachathespire 首创;theme.json 解释器与原生冻结呈现面是第二/三消费者,故进本库)

# 一行等距图标(可作进度轨道)。坐标全部屏幕像素(设计矩形先经 DesignSpace 换算);
# cx = 行中心。返回 {row: Array[Control], geom: {x, span, size, gap, count}}
# —— geom 供 gap_dots / mask_track 复用同一套行几何。
# st: {count, size, gap, cx, cy | bottom, pivot,
#      file 或 file_pattern + index_base, nearest, placeholder_color}
func icon_row(parent: Control, st: Dictionary) -> Dictionary:
	_check("icon_row", st, ["count", "size", "gap", "cx", "cy", "bottom", "pivot",
		"file", "file_pattern", "index_base", "nearest", "placeholder_color"])
	var count := maxi(1, int(st.get("count", 1)))
	var size: float = st.get("size", 32.0)
	var gap: float = st.get("gap", 0.0)
	var span: float = count * size + (count - 1.0) * gap
	var x: float = st.get("cx", 0.0) - span * 0.5
	var row: Array = []
	for i in count:
		var file := str(st.get("file", "")) if st.has("file") \
			else str(st.get("file_pattern", "%d")) % (int(st.get("index_base", 1)) + i)
		var rect: Rect2
		if st.has("bottom"):
			# 底边锚:放大时底边不动、原地向上长大(pivot "bottom" 配对)
			rect = Rect2(Vector2(x + i * (size + gap), st.get("bottom", 0.0) - size),
				Vector2(size, size))
		else:
			var cy: float = st.get("cy", 0.0)
			rect = Rect2(Vector2(x + i * (size + gap), cy - size * 0.5), Vector2(size, size))
		row.append(_tex_or_placeholder(parent, file, rect,
			st.get("placeholder_color", Color.GRAY), st.get("nearest", true)))
		_apply_pivot(row[i], rect, str(st.get("pivot", "center")))
	return {"row": row, "geom": {"x": x, "span": span, "size": size, "gap": gap, "count": count}}


# 当前行放大标记:stage1 从 1 计;只写 Scale 变换(矩形终身不变,突发冻结期照常生效)
func mark_stage(row: Array, stage1: int, factor: float) -> void:
	if row.is_empty():
		return
	var idx: int = clampi(stage1, 1, row.size()) - 1
	for i in row.size():
		row[i].scale = Vector2(factor, factor) if i == idx else Vector2.ONE


# 行间隙常驻圆点:几何取自 icon_row 的返回 geom(圆心 = 相邻两图标间隙中点)。
# st: {dot_scale, color, cy};返回各圆点 Rect2(供蒙版副本复用)
func gap_dots(parent: Control, geom: Dictionary, st: Dictionary) -> Array:
	_check("gap_dots", st, ["dot_scale", "color", "cy"])
	var size: float = float(geom.size) * float(st.get("dot_scale", 0.2))
	var cy: float = st.get("cy", 0.0)
	var circle: ImageTexture = circle_tex()
	var rects: Array = []
	for j in int(geom.count) - 1:
		var dcx: float = float(geom.x) + (float(j) + 1.0) * float(geom.size) \
			+ float(j) * float(geom.gap) + float(geom.gap) * 0.5
		var rect := Rect2(Vector2(dcx - size * 0.5, cy - size * 0.5), Vector2(size, size))
		tex_rect(parent, {"tex": circle, "rect": rect, "modulate": st.get("color", Color.WHITE)})
		rects.append(rect)
	return rects


# 剪贴蒙版暗层(gachathespire 首创):贴图按 tint 暗调,仅轨分数段 [seg_a, seg_b]
# 内可见;nf_* 构建期烘焙各节点在轨上的分数几何。进度更新只写 seg_a/seg_b
# (全节点同值)—— set_shader_parameter 走 RenderingServer,不触 Control 矩形,
# 同步突发冻结期照常生效(与第一排放大标记的变换路径是同一约束)。
class MaskFill extends RefCounted:
	var _mats: Array = []
	var _shader: Shader

	const FILL_GLSL := """shader_type canvas_item;
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
}"""

	func _init(glsl := FILL_GLSL) -> void:
		_shader = Shader.new()
		_shader.code = glsl

	func add(parent: Control, tex: Texture2D, rect: Rect2,
			nf_left: float, nf_width: float, tint: Color) -> void:
		var t := TextureRect.new()
		t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:必须先于 texture
		t.texture = tex
		t.stretch_mode = TextureRect.STRETCH_SCALE
		t.position = rect.position
		t.size = rect.size
		t.mouse_filter = Control.MOUSE_FILTER_IGNORE
		var mat := ShaderMaterial.new()
		mat.shader = _shader
		mat.set_shader_parameter("tint", tint)
		mat.set_shader_parameter("nf_left", nf_left)
		mat.set_shader_parameter("nf_width", nf_width)
		mat.set_shader_parameter("seg_a", 0.0)
		mat.set_shader_parameter("seg_b", -1.0)  # 空段:首帧填充前不可见
		t.material = mat
		parent.add_child(t)
		_mats.append(mat)

	# 段参数写入全部副本材质(全节点同值):确定 = [0, local];不定 = 1/4 宽滑段扫过
	func segment(local: float, indeterminate: bool, t: float, cycle_s: float) -> void:
		var a := 0.0
		var b := 0.0
		if indeterminate:
			a = fposmod(t / maxf(0.1, cycle_s), 1.0) * 0.75
			b = a + 0.25
		else:
			a = 0.0
			b = clampf(local, 0.0, 1.0)
		for m in _mats:
			m.set_shader_parameter("seg_a", a)
			m.set_shader_parameter("seg_b", b)


# 蒙版轨道工厂:members = [{tex(Texture2D | null → 白方块占位), rect, tint}],
# 轨分数域 = icon_row 的 geom(段 [0,1] 映射到行左缘→右缘)。
func mask_track(parent: Control, geom: Dictionary, members: Array) -> MaskFill:
	var mask := MaskFill.new()
	var span := maxf(1.0, float(geom.span))
	for m in members:
		var tex: Texture2D = m.get("tex")
		if tex == null:
			tex = white_tex()
		var rect: Rect2 = m.get("rect", Rect2())
		mask.add(parent, tex, rect,
			(rect.position.x - float(geom.x)) / span, rect.size.x / span,
			m.get("tint", Color.WHITE))
	return mask


# ================================================================ 活动日志窗
# entries = boot 已去重、已格式化的活动流(只读);窗口淘汰策略在内部。

class LogWindow extends RefCounted:
	var _labels: Array = []
	var _lines := 1
	var _per_line := 1
	var _sep := ""
	var _column := true
	var _last_signature := ""

	func _init(labels: Array, lines: int, per_line: int, sep: String, column: bool) -> void:
		_labels = labels
		_lines = lines
		_per_line = per_line
		_sep = sep
		_column = column

	func render(entries: Array) -> void:
		# 幂等守卫:流没变就不碰标签(逐帧 apply 时的兜底)
		var sig := str(entries.size()) + ":" + str(entries.hash())
		if sig == _last_signature:
			return
		_last_signature = sig
		if _column:
			_render_column(entries)
		else:
			_render_rows(entries)

	func _render_column(entries: Array) -> void:
		var off: int = entries.size() - _labels.size()
		for i in _labels.size():
			_labels[i].text = str(entries[i + off]) if i + off >= 0 else ""

	func _render_rows(entries: Array) -> void:
		# 整行淘汰:超上限丢最旧的整行,最新永远在最后一行
		var cap := _lines * _per_line
		var usable := entries
		if usable.size() > cap:
			usable = usable.slice(usable.size() - cap)
			usable = usable.slice(usable.size() % _per_line)  # 对齐行边界
		for i in _labels.size():
			var start := i * _per_line
			if start >= usable.size():
				_labels[i].text = ""
				continue
			var chunk := usable.slice(start, start + _per_line)
			var parts := PackedStringArray()
			for e in chunk:
				parts.append(str(e))
			_labels[i].text = _sep.join(parts)


# 竖列日志(classic/minespire):最新在底部,越旧越淡(alpha 渐变烘进颜色)。
# {x,y,lines,line_h,font,color}
func log_column(parent: Control, st: Dictionary) -> LogWindow:
	_check("log_column", st, ["x", "y", "lines", "line_h", "font", "color"])
	var lines: int = st.get("lines", 10)
	var line_h: float = st.get("line_h", 17.0)
	var color: Color = st.get("color", Color.WHITE)
	var labels: Array = []
	for i in lines:
		var a: float = 0.3 + 0.65 * float(i + 1) / float(lines)
		labels.append(label(parent, {
			"x": st.get("x", 0.0),
			"y": float(st.get("y", 0.0)) + float(i) * line_h,
			"font": st.get("font", 12),
			"color": Color(color.r, color.g, color.b, a),
		}))
	return LogWindow.new(labels, lines, 1, "", true)


# 整行淘汰日志(gachathespire 3×5):{x,y,w,lines,per_line,sep,line_h,font,color,align,overrun}
func log_rows(parent: Control, st: Dictionary) -> LogWindow:
	_check("log_rows", st, ["x", "y", "w", "lines", "per_line", "sep", "line_h", "font", "color", "align", "overrun"])
	var lines: int = st.get("lines", 3)
	var line_h: float = st.get("line_h", 12.0)
	var labels: Array = []
	for i in lines:
		labels.append(label(parent, {
			"x": st.get("x", 0.0),
			"y": float(st.get("y", 0.0)) + float(i) * line_h,
			"w": st.get("w", 0.0),
			"h": line_h,
			"font": st.get("font", 12),
			"color": st.get("color", Color.WHITE),
			"align": st.get("align", HORIZONTAL_ALIGNMENT_CENTER),
			"overrun": st.get("overrun", TextServer.OVERRUN_TRIM_ELLIPSIS),
		}))
	return LogWindow.new(labels, lines, int(st.get("per_line", 5)), str(st.get("sep", " | ")), false)


# ================================================================ 精灵表动画

class SpriteAnimator extends Node:
	var _atlas: AtlasTexture
	var _frame_h := 0.0
	var _frames := 1
	var _fps := 12.0
	var _elapsed := 0.0
	var stopped := false  # retire 后不再推进(画面即将隐藏)

	func _init(atlas: AtlasTexture, frame_h: float, frames: int, fps: float) -> void:
		_atlas = atlas
		_frame_h = frame_h
		_frames = maxi(1, frames)
		_fps = maxf(0.1, fps)

	# 自然帧驱动:同步突发期 _process 不跑 → 自动冻结(与滑段同语义)
	func _process(delta: float) -> void:
		if stopped or _atlas == null:
			return
		_elapsed += delta
		var fw: float = _atlas.region.size.x
		_atlas.region = Rect2(0.0,
			float(int(_elapsed * _fps) % _frames) * _frame_h,
			fw, _frame_h)


# {file,rect,frame_w,frame_h,frames,fps,nearest} —— 缺素材返回 null,调用方跳过
func sprite(parent: Control, st: Dictionary) -> SpriteAnimator:
	_check("sprite", st, ["file", "rect", "frame_w", "frame_h", "frames", "fps", "nearest"])
	var sheet := load_texture(str(st.get("file", "")))
	if sheet == null:
		return null
	var rect: Rect2 = st.get("rect", Rect2())
	var frame_w: float = st.get("frame_w", 0.0)
	var frame_h: float = st.get("frame_h", 0.0)
	var atlas := AtlasTexture.new()
	atlas.atlas = sheet
	atlas.region = Rect2(0.0, 0.0, frame_w, frame_h)
	var t := TextureRect.new()
	t.expand_mode = TextureRect.EXPAND_IGNORE_SIZE  # 钳制陷阱:必须先于 texture
	t.texture = atlas
	t.position = rect.position
	t.size = rect.size
	t.stretch_mode = TextureRect.STRETCH_SCALE
	if st.get("nearest", true):
		t.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
	t.mouse_filter = Control.MOUSE_FILTER_IGNORE
	parent.add_child(t)
	var animator := SpriteAnimator.new(atlas, frame_h, int(st.get("frames", 1)), st.get("fps", 12.0))
	parent.add_child(animator)
	return animator


# ================================================================ 相位数学(不定进度)

# 滑段行程分数:0 → 1(头部从轨左缘跑到 0.75 轨宽处,右缘恰好可达轨满)。
# travel 由调用方乘(BarFill.slide / MaskFill.segment)——勿在此预乘,
# 双重 0.75 会让滑段冲不满一程。各主题共用,勿在主题里重写。
func slide_phase(t: float, cycle_s: float) -> float:
	return fposmod(t / maxf(0.1, cycle_s), 1.0)


# classic shimmer:宽度呼吸(三角波)
func pulse_width(t: float, min_w: float, travel: float) -> float:
	return min_w + absf(fmod(t * 0.8, 2.0) - 1.0) * travel


# ================================================================ 生成纹理

# 纯白圆点(alpha 边缘 1px 抗锯齿;着色走 modulate / 暗色副本 tint)
func circle_tex(size := 32) -> ImageTexture:
	var r := float(size) * 0.45  # 有效半径 45%:留 1px 淡出带
	var c := (float(size) - 1.0) * 0.5
	var img := Image.create_empty(size, size, false, Image.FORMAT_RGBA8)
	for y in size:
		for x in size:
			var d := Vector2(float(x) - c, float(y) - c).length()
			img.set_pixel(x, y, Color(1.0, 1.0, 1.0, clampf(r - d + 0.5, 0.0, 1.0)))
	return ImageTexture.create_from_image(img)


# 缺图占位 slot 的纯白小方块(着色 = 占位色 × tint)
func white_tex() -> ImageTexture:
	var img := Image.create_empty(4, 4, false, Image.FORMAT_RGBA8)
	img.fill(Color.WHITE)
	return ImageTexture.create_from_image(img)


# ================================================================ 素材 / 校验

# mod_dir 相对路径贴图;缺席/损坏返回 null(主题各自优雅降级)
func load_texture(rel: String) -> ImageTexture:
	if _mod_dir == "" or rel == "":
		return null
	var path := _mod_dir.path_join(rel)
	if not FileAccess.file_exists(path):
		return null
	var img := Image.new()
	if img.load_png_from_buffer(FileAccess.get_file_as_bytes(path)) != OK:
		return null
	return ImageTexture.create_from_image(img)


# 样式校验:未知键即报错(preview 每轮输出,相当于编译期检查)
func _check(widget: String, st: Dictionary, known: Array) -> void:
	for k in st.keys():
		if not k in known:
			var names := PackedStringArray()
			for n in known:
				names.append(str(n))
			push_error("[kit] %s 未知样式键 '%s'(可用: %s)" % [widget, k, ", ".join(names)])
