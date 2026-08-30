# classic 主题 —— 底部双条 + 条带上方滚动活动日志列。
# 屏幕坐标(无设计矩形);常量块即样式,改这里就是改主题。
# 不定进度 = 宽度呼吸(kit.pulse_width)。
extends Node

const TRACK_COLOR := Color(1, 1, 1, 0.15)
const DETAIL_COLOR := Color(0.62, 0.64, 0.70, 1)
const FILL_COLOR := Color(0.2, 0.85, 0.9, 1)
const OVERALL_FILL := Color(0.2, 0.85, 0.9, 0.55)
const PAD := 24.0
const STRIP_H := 76.0
const STEP_Y := 6.0
const DETAIL_Y := 31.0
const OVERALL_Y := 55.0
const OVERALL_H := 3.0
const LOCAL_Y := 66.0
const LOCAL_H := 5.0
const PULSE_MIN := 60.0
const PULSE_TRAVEL := 160.0
const LOG_LINES := 10
const LOG_LINE_H := 17.0
const LOG_FONT := 12

var _kit
var _step: Label
var _detail: Label
var _overall  # kit.BarFill
var _local    # kit.BarFill
var _log      # kit.LogWindow
var _retired := false


func theme_build(ctx: Dictionary) -> void:
	_kit = ctx.kit
	var vs: Vector2 = ctx.viewport
	var strip := Control.new()
	strip.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	strip.offset_top = -STRIP_H
	ctx.root.add_child(strip)

	_step = _kit.label(strip, {
		"text": ctx.txt.call("bar.starting"), "x": PAD, "y": STEP_Y,
		"font": 20, "color": Color.WHITE})
	_detail = _kit.label(strip, {
		"text": "engine boot", "x": PAD, "y": DETAIL_Y,
		"font": 14, "color": DETAIL_COLOR})
	_overall = _kit.bar_solid(strip, {
		"x": PAD, "y": OVERALL_Y, "w": vs.x - PAD * 2.0, "h": OVERALL_H,
		"track_color": TRACK_COLOR, "fill_color": OVERALL_FILL})
	_local = _kit.bar_solid(strip, {
		"x": PAD, "y": LOCAL_Y, "w": vs.x - PAD * 2.0, "h": LOCAL_H,
		"track_color": TRACK_COLOR, "fill_color": FILL_COLOR})
	# 活动日志:条带上方、向上滚动(Control 默认不裁剪子节点,负 y 在条带上缘之上)
	_log = _kit.log_column(strip, {
		"x": PAD, "y": -(float(LOG_LINES) * LOG_LINE_H + 4.0),
		"lines": LOG_LINES, "line_h": LOG_LINE_H, "font": LOG_FONT,
		"color": DETAIL_COLOR})


func theme_apply(snap: Dictionary) -> void:
	if _retired:
		return
	_overall.set_fraction(snap.overall)
	if snap.indeterminate:
		_local.set_width_px(_kit.pulse_width(snap.t, PULSE_MIN, PULSE_TRAVEL))
	else:
		_local.set_fraction(snap.local)
	_step.text = snap.step
	_detail.text = snap.detail
	_log.render(snap.log_entries)


func theme_retire() -> void:
	_retired = true
