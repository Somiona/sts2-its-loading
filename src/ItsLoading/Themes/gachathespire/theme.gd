# gachathespire 主题 —— 抽卡游戏风:白底双排徽记 + 剪贴蒙版填充 + 整行淘汰日志。
#
# 两排徽记图标就是进度条:第一排 7 枚标记总进度(当前阶段 ENLARGE× 放大,
# pivot=底边中点 + Scale,矩形终身不变);第二排 7 枚 85% 尺寸 + 图标间隙的
# 常驻小圆,阶段进度 = PS 剪贴蒙版式深色填充:与基图同形的暗色副本
# (×FILL_TINT)只在轨分数段 [seg_a, seg_b] 内可见,段随 local 从左往右长。
# 不定进度 = 1/4 宽滑段在同一蒙版形状里扫过。进度区不写任何文字。
#
# 剪贴蒙版(MaskFill)是单一消费者机制,留在本文件;第二个消费者出现时再搬进 kit。
extends Node

const BG := Color(1, 1, 1, 1)
const TEXT := Color(0.349, 0.349, 0.349, 1)          # 35% 灰 #595959
const VERSION_COLOR := Color(0.847, 0.847, 0.847, 1)  # 15% 灰 #D9D9D9
const FILL_TINT := Color(0.5, 0.5, 0.5, 1)           # 基图 50% 灰 → 25% 亮度(≈PS 75% 灰)
const DOT_COLOR := Color(0.502, 0.502, 0.502, 1)     # 常驻小圆基色(与徽记同灰阶)
const PLACEHOLDER := Color(0.502, 0.502, 0.502, 1)   # 缺图槽位灰方块
const DESIGN_W := 854.0
const DESIGN_H := 480.0
const LOGO_Y := 56.0
const LOGO_W := 520.0
const FALLBACK_FONT := 28
const ICONS := 7          # = BootStage 1..7(第二排同数,子步离散化)
const ICON_SIZE := 44.0
const ICON_GAP := 20.0
const ROW1_CY := 308.0    # 行组居中偏下,与 log 块留出空档
const ENLARGE := 1.2      # 当前阶段放大倍数(底边中点锚,原地放大)
const SUB_SCALE := 0.85
const SUB_GAP := 12.0
const SUB_CY := 366.0     # 更小更密
const DOT_SCALE := 0.2    # 小圆直径 = 第二排图标尺寸的 1/5
const LOG_LINES := 3
const LOG_PER_LINE := 5   # 整行淘汰的活动日志窗口
const LOG_SEP := " | "    # 条目内已含「·」,故避开
const LOG_FONT := 8.4     # 刻意小号,进度区之外的信息不抢注意力
const LOG_LINE_H := 12.0
const LOG_BOTTOM := 30.0
const LOG_SIDE_PAD := 24.0
const LOG_COLOR := Color(0.502, 0.502, 0.502, 1)
const VERSION_LEFT := 24.0
const VERSION_TOP := 24.0  # 左上角,比日志字稍小
const VERSION_FONT := 7.5
const CYCLE_S := 3.0       # 不定滑段跑满一程(kit.slide_phase 配对)

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

# 剪贴蒙版暗层:贴图按 tint 暗调,仅轨分数段 [seg_a, seg_b] 内可见;
# nf_* 构建期烘焙各节点在轨上的分数几何。进度更新只写 seg_a/seg_b(全节点同值)
# —— set_shader_parameter 走 RenderingServer,不触 Control 矩形,
# 同步突发冻结期照常生效(与第一排放大标记的变换路径是同一约束)。
class MaskFill extends RefCounted:
	var _mats: Array = []
	var _shader: Shader

	func _init(glsl: String) -> void:
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

	# 段参数写入全部副本材质(全节点同值):确定 = [0, local];不定 = 滑段扫过
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


var _kit
var _row1: Array = []   # 第一排控件(TextureRect / 缺图占位 ColorRect)
var _mask: MaskFill
var _log                # kit.LogWindow
var _retired := false


func theme_build(ctx: Dictionary) -> void:
	_kit = ctx.kit
	var d = _kit.design(ctx.viewport, DESIGN_W, DESIGN_H)
	_mask = MaskFill.new(FILL_GLSL)
	var circle: ImageTexture = _kit.circle_tex()
	var white: ImageTexture = _kit.white_tex()
	_kit.bg(ctx.root, {"color": BG})
	_kit.logo(ctx.root, {
		"file": "gachathespire/gachathespire_logo.png",
		"x": d.p((DESIGN_W - LOGO_W) * 0.5, LOGO_Y).x, "y": d.p(0.0, LOGO_Y).y,
		"w": d.s(LOGO_W), "fallback_text": "SLAY THE SPIRE 2",
		"fallback_font": d.font(FALLBACK_FONT), "fallback_color": TEXT,
		"nearest": false})

	# 第二排(当前进度):小而密。z 序:基图 → 基圆 → 暗色副本层(副本只属于
	# 第二排,第一排最后叠上)。剪贴蒙版:图标+小圆是蒙版形状,深色内容只在
	# 轨分数段内可见。
	var s2: float = ICON_SIZE * SUB_SCALE
	var span2: float = ICONS * s2 + (ICONS - 1.0) * SUB_GAP
	var x2: float = (DESIGN_W - span2) * 0.5
	var dot: float = s2 * DOT_SCALE
	for j in ICONS - 1:
		var dcx: float = x2 + (float(j) + 1.0) * s2 + float(j) * SUB_GAP + SUB_GAP * 0.5
		var drect: Rect2 = d.center_rect(dcx, SUB_CY, dot)
		_kit.tex_rect(ctx.root, {"tex": circle, "rect": drect, "modulate": DOT_COLOR})
		_mask.add(ctx.root, circle, drect, (dcx - dot * 0.5 - x2) / span2, dot / span2,
			DOT_COLOR * FILL_TINT)
	for i in ICONS:
		var irect: Rect2 = d.center_rect(x2 + i * (s2 + SUB_GAP) + s2 * 0.5, SUB_CY, s2)
		var file := "gachathespire/gachathespire_%d.png" % (ICONS + i + 1)
		_kit.icon(ctx.root, {"file": file, "rect": irect,
			"placeholder_color": PLACEHOLDER, "nearest": false})
		# 副本基色 = 白(缺图时的白方块占位)或徽记纹理自身灰阶,×FILL_TINT 后同为深灰档
		var itex: ImageTexture = _kit.load_texture(file)
		_mask.add(ctx.root, itex if itex != null else white, irect,
			float(i) * (s2 + SUB_GAP) / span2, s2 / span2, FILL_TINT)

	# 第一排(总进度):当前阶段 ENLARGE× 放大(底边中点锚 + Scale,矩形终身不变)
	var row_bottom: float = ROW1_CY + ICON_SIZE * 0.5
	var span1: float = ICONS * ICON_SIZE + (ICONS - 1.0) * ICON_GAP
	var x1: float = (DESIGN_W - span1) * 0.5
	for i in ICONS:
		var cx: float = x1 + i * (ICON_SIZE + ICON_GAP) + ICON_SIZE * 0.5
		_row1.append(_kit.icon(ctx.root, {
			"file": "gachathespire/gachathespire_%d.png" % (i + 1),
			"rect": d.bottom_rect(cx, row_bottom, ICON_SIZE),
			"placeholder_color": PLACEHOLDER, "pivot": "bottom", "nearest": false}))
	_set_stage(1)  # 工坊期(阶段 1)起即有放大标记

	_log = _kit.log_rows(ctx.root, {
		"x": d.p(LOG_SIDE_PAD, 0.0).x,
		"y": d.p(0.0, DESIGN_H - LOG_BOTTOM - LOG_LINES * LOG_LINE_H).y,
		"w": d.s(DESIGN_W - 2.0 * LOG_SIDE_PAD), "line_h": d.s(LOG_LINE_H),
		"lines": LOG_LINES, "per_line": LOG_PER_LINE, "sep": LOG_SEP,
		"font": d.font(LOG_FONT), "color": LOG_COLOR})

	_kit.label(ctx.root, {
		"text": "It's Loading v" + str(ctx.mod_version),
		"x": d.p(VERSION_LEFT, 0.0).x, "y": d.p(0.0, VERSION_TOP).y,
		"font": d.font(VERSION_FONT), "color": VERSION_COLOR})


func theme_apply(snap: Dictionary) -> void:
	if _retired:
		return
	if snap.stage_changed:
		_set_stage(int(snap.stage))
	_mask.segment(float(snap.local), bool(snap.indeterminate), float(snap.t), CYCLE_S)
	_log.render(snap.log_entries)


# 第一排放大标记:当前阶段 Scale=ENLARGE、其余 1.0。只写变换路径,
# 永不触发「改尺寸→等重绘」(突发冻结期 Size 写入会滞留旧尺寸)。
func _set_stage(stage: int) -> void:
	var idx: int = clampi(stage, 1, ICONS) - 1
	for i in _row1.size():
		_row1[i].scale = Vector2(ENLARGE, ENLARGE) if i == idx else Vector2.ONE


func theme_retire() -> void:
	_retired = true
