# minespire 主题 —— Minecraft 风整屏红、居中双描边条、左下活动日志列、右下奔跑狐狸。
# 854×480 设计矩形等比缩放居中;不定进度 = 1/4 宽滑段扫过(kit.slide_phase)。
extends Node

const BG := Color(0.937, 0.196, 0.239, 1)  # #ef323d
const TEXT := Color(1, 1, 1, 1)
const DIM := Color(1, 1, 1, 0.8)
const OVERALL_FILL := Color(1, 1, 1, 0.75)  # 总体条略淡,主视觉留给阶段条
const DESIGN_W := 854.0
const DESIGN_H := 480.0
const BAR_W := 400.0
const BAR_H := 20.0
const BARS_TOP := 250.0     # 条块顶;标签依次向上/向下排
const LABEL_GAP := 4.0      # 标签 ↔ 条
const BAR_GAP := 5.0        # 条 ↔ 下一标签
const STEP_LABEL_H := 26.0
const DETAIL_LABEL_H := 19.0
const BORDER_W := 2.0       # 描边宽(nine-slice bg 边)
const FILL_INSET := 4.0     # 填充内缩(nine-slice fg 边)
const LOGO_Y := 96.0
const LOGO_W := 520.0
const FALLBACK_FONT := 28
const STEP_FONT := 20
const DETAIL_FONT := 14
const LOG_LEFT := 10.0
const LOG_BOTTOM := 10.0
const LOG_LINES := 10
const LOG_LINE_H := 17.0
const LOG_FONT := 10
const FOX_W := 151.0
const FOX_H := 128.0
const FOX_FRAMES := 28
const FOX_FPS := 12.0
const FOX_RIGHT := 10.0
const FOX_BOTTOM := 30.0    # 版本号上方
const VERSION_RIGHT := 10.0
const VERSION_BOTTOM := 10.0
const CYCLE_S := 3.0        # 不定滑段跑满一程(kit.slide_phase 配对)

var _kit
var _step: Label
var _detail: Label
var _overall  # kit.BarFill
var _local    # kit.BarFill
var _log      # kit.LogWindow
var _retired := false


func theme_build(ctx: Dictionary) -> void:
	_kit = ctx.kit
	var d = _kit.design(ctx.viewport, DESIGN_W, DESIGN_H)
	_kit.bg(ctx.root, {"color": BG})
	_kit.logo(ctx.root, {
		"file": "minespire/mc_style_sts2_logo.png",
		"x": d.p((DESIGN_W - LOGO_W) * 0.5, LOGO_Y).x, "y": d.p(0.0, LOGO_Y).y,
		"w": d.s(LOGO_W), "fallback_text": "SLAY THE SPIRE 2",
		"fallback_font": d.font(FALLBACK_FONT), "fallback_color": TEXT,
		"nearest": true})

	# 条块:step 标签 → 总体条 → detail 标签 → 阶段条(Minecraft 风的标签/条间距节奏)
	var bar_x: float = d.p((DESIGN_W - BAR_W) * 0.5, 0.0).x
	var y := BARS_TOP
	_step = _kit.label(ctx.root, {
		"text": ctx.txt.call("bar.starting"), "x": bar_x, "y": d.p(0.0, y).y,
		"font": d.font(STEP_FONT), "color": TEXT})
	y += STEP_LABEL_H + LABEL_GAP
	_overall = _kit.bar_outline(ctx.root, {
		"x": bar_x, "y": d.p(0.0, y).y, "w": d.s(BAR_W), "h": d.s(BAR_H),
		"border_w": d.s(BORDER_W), "inset": d.s(FILL_INSET),
		"border_color": TEXT, "fill_color": OVERALL_FILL})
	y += BAR_H + BAR_GAP
	_detail = _kit.label(ctx.root, {
		"text": "engine boot", "x": bar_x, "y": d.p(0.0, y).y,
		"font": d.font(DETAIL_FONT), "color": DIM})
	y += DETAIL_LABEL_H + LABEL_GAP
	_local = _kit.bar_outline(ctx.root, {
		"x": bar_x, "y": d.p(0.0, y).y, "w": d.s(BAR_W), "h": d.s(BAR_H),
		"border_w": d.s(BORDER_W), "inset": d.s(FILL_INSET),
		"border_color": TEXT, "fill_color": TEXT})

	_log = _kit.log_column(ctx.root, {
		"x": d.p(LOG_LEFT, 0.0).x,
		"y": d.p(0.0, DESIGN_H - LOG_BOTTOM - LOG_LINES * LOG_LINE_H).y,
		"lines": LOG_LINES, "line_h": d.s(LOG_LINE_H), "font": d.font(LOG_FONT),
		"color": TEXT})

	# 奔跑狐狸(© NeoForged contributors,LGPL-2.1,FancyModLoader):
	# 素材缺席只少一只狐狸(kit.sprite 返回 null 全流程跳过)
	_kit.sprite(ctx.root, {
		"file": "minespire/fox_running.png",
		"rect": Rect2(d.p(DESIGN_W - FOX_RIGHT - FOX_W, DESIGN_H - FOX_BOTTOM - FOX_H),
			Vector2(d.s(FOX_W), d.s(FOX_H))),
		"frame_w": FOX_W, "frame_h": FOX_H, "frames": FOX_FRAMES,
		"fps": FOX_FPS, "nearest": true})

	_kit.label(ctx.root, {
		"text": "It's Loading v" + str(ctx.mod_version),
		"x": d.p(DESIGN_W - VERSION_RIGHT - 300.0, 0.0).x,
		"y": d.p(0.0, DESIGN_H - VERSION_BOTTOM - 16.0).y,
		"w": d.s(300.0), "h": d.s(16.0), "font": d.font(12), "color": DIM,
		"align": HORIZONTAL_ALIGNMENT_RIGHT})


func theme_apply(snap: Dictionary) -> void:
	if _retired:
		return
	_overall.set_fraction(snap.overall)
	if snap.indeterminate:
		_local.slide(_kit.slide_phase(snap.t, CYCLE_S))
	else:
		_local.set_fraction(snap.local)
	_step.text = snap.step
	_detail.text = snap.detail
	_log.render(snap.log_entries)


func theme_retire() -> void:
	_retired = true
