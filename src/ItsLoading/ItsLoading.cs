using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ItsLoading;

/// <summary>
/// v0.5 — 单一底部进度条,贯穿整个启动过程。
/// 设计原则(吃过的亏):
///   1. 不用 Container/CenterContainer —— 其布局走 deferred 排序,同步突发期间不执行(内容挤在 0×0)
///   2. 全部节点手动定位 —— v0.2 底部条验证过的唯一可靠模式
///   3. gd 与 C# 渲染完全一致的样式 —— frame 0 接管无视觉跳变
///   4. 单一进度刻度 0→1:工坊读取 0-0.25 / mod 加载 0.25-0.60 / Essential 0.60-0.92 / 菜单 0.92-1.0
/// </summary>
[ModInitializer("Init")]
public static class ItsLoading
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static long _lastMs;
    private static int _count = 1; // 本 mod 自己算第 1 个
    private static int _total = 1;
    private static bool _done;
    private static bool _menuHandled;
    private static bool _injectedThisRun;

    private static CanvasLayer _layer;
    private static Label _stepLabel;   // 第一行:当前步骤 + 计数
    private static Label _detailLabel; // 第二行:当前对象 + 耗时
    private static ColorRect _fill;    // 进度填充
    private static float _barFullWidth;

    private static bool UiOk => _layer != null;

    // 条移除后 AssetLoadingSession.Process 的 postfix 仍可能触发(游戏内房间加载等),
    // 用死亡标志防止对已释放节点的操作每帧抛异常
    private static bool _barDead;

    public static void Init()
    {
        Log.Warn($"[ItsLoading] v{typeof(ItsLoading).Assembly.GetName().Version} initializer " +
                 $"@ +{Sw.ElapsedMilliseconds}ms frame={Engine.GetFramesDrawn()}");
        _total = Math.Max(1, ModManager.Mods.Count);
        // 时钟对表:把 C# Stopwatch 时间轴换算到引擎时间轴(gd 第 0 帧起算)
        Recorder.EngineOffsetMs = (long)Time.GetTicksMsec() - Sw.Elapsed.TotalMilliseconds;
        Run("ensure boot splash installed", EnsureBootSplashInstalled);
        Run("ensure first in load order", EnsureFirstInLoadOrder);
        // 顺序关键:先建好条并强制绘制一帧,再隐藏 autoload splash,保证无黑屏间隙
        Run("build bar", BuildBar);
        Run("boot splash handoff", HandoffFromBootSplash);
        Run("patch loader", PatchLoader);
        Run("patch boot phases", PatchBootPhases);
        Run("patch mod info icon", PatchModInfoIcon);
        Log.Warn($"[ItsLoading] watching {_total} mods");
        SetProgress(0.25f, $"模组加载 1/{_total}", "", true);
    }

    // ---------------------------------------------------------------- mod 菜单图标
    //
    // 游戏读图标走 ResourceLoader("res://<id>/mod_image.png"),而导出版 Godot
    // 无法加载未导入的裸 PNG(需要 BaseLib 那种 .godot/imported/*.ctex + remap 链)。
    // 所以改为 patch mod 信息面板:从 mod 目录磁盘直接读图,运行时 Image API 认裸 PNG。

    private static void PatchModInfoIcon()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.icon");
        var fill = AccessTools.Method(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer"),
            "Fill");
        if (fill == null)
        {
            Log.Warn("[ItsLoading] NModInfoContainer.Fill not found — icon patch skipped");
            return;
        }
        harmony.Patch(fill, postfix: new HarmonyMethod(typeof(ItsLoading), nameof(AfterModInfoFill)));
        Log.Warn("[ItsLoading] mod info icon patch installed");
    }

    private static void AfterModInfoFill(object __instance, Mod mod)
    {
        if (mod?.manifest?.id != "ItsLoading") return;
        Run("set mod icon", () =>
        {
            string imgPath = Path.Combine(mod.path, "mod_image.png");
            if (!File.Exists(imgPath))
            {
                Log.Warn("[ItsLoading] mod_image.png not found next to dll: " + imgPath);
                return;
            }
            var image = Image.LoadFromFile(imgPath);
            var tex = ImageTexture.CreateFromImage(image);
            var rect = (TextureRect)AccessTools.Field(
                AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer"),
                "_image").GetValue(__instance);
            if (rect != null && tex != null)
            {
                rect.Texture = tex;
                Log.Warn("[ItsLoading] mod menu icon set from " + imgPath);
            }
        });
    }

    private static void Run(string what, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to {what}: {e}");
        }
    }

    // ================================================================ UI(全手动定位,无 Container)

    private static void BuildBar()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 vs = tree.Root.GetVisibleRect().Size;

        _layer = new CanvasLayer { Layer = 999 };

        // 无垫底:仅作定位用的透明容器(不渲染任何背景)
        var strip = new Control();
        strip.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        strip.OffsetTop = -64f;
        _layer.AddChild(strip);

        _stepLabel = new Label();
        _stepLabel.Position = new Vector2(24f, 8f);
        _stepLabel.AddThemeFontSizeOverride("font_size", 20);
        _stepLabel.AddThemeColorOverride("font_color", Colors.White);
        _stepLabel.Text = $"模组加载 1/{_total}";
        strip.AddChild(_stepLabel);

        _detailLabel = new Label();
        _detailLabel.Position = new Vector2(24f, 36f);
        _detailLabel.AddThemeFontSizeOverride("font_size", 14);
        _detailLabel.AddThemeColorOverride("font_color", new Color(0.62f, 0.64f, 0.70f, 1f));
        _detailLabel.Text = "";
        strip.AddChild(_detailLabel);

        _barFullWidth = vs.X - 48f;
        var track = new ColorRect();
        track.Position = new Vector2(24f, 56f);
        track.Size = new Vector2(_barFullWidth, 5f);
        track.Color = new Color(1f, 1f, 1f, 0.15f);
        strip.AddChild(track);

        _fill = new ColorRect();
        _fill.Position = Vector2.Zero;
        _fill.Size = new Vector2(0f, 5f);
        _fill.Color = new Color(0.2f, 0.85f, 0.9f, 1f);
        track.AddChild(_fill);

        // 首次注入提示(左上角,不挡条)
        if (_injectedThisRun)
        {
            var hint = new Label();
            hint.Text = "It's Loading · 已注入启动画面,自下次启动起全程可见";
            hint.Position = new Vector2(24f, 24f);
            hint.AddThemeFontSizeOverride("font_size", 14);
            hint.AddThemeColorOverride("font_color", new Color(0.2f, 0.85f, 0.9f, 1f));
            _layer.AddChild(hint);
            var t = tree.CreateTimer(8.0);
            t.Timeout += () => Run("hide injection hint", () => hint.QueueFree());
        }

        // 必须直接 AddChild:同步突发期间 deferred 队列永远不会执行
        tree.Root.AddChild(_layer);
        RenderingServer.ForceDraw();
        Log.Warn($"[ItsLoading] bar attached (viewport {vs.X}x{vs.Y})");
    }

    /// <summary>唯一的进度更新入口:条宽 + 两行文案 + 强制出帧。</summary>
    private static void SetProgress(float frac, string step, string detail, bool forceDraw = true)
    {
        if (!UiOk || _barDead) return;
        try
        {
            _fill.Size = new Vector2(_barFullWidth * Math.Clamp(frac, 0f, 1f), 5f);
            if (step != null) _stepLabel.Text = step;
            if (detail != null) _detailLabel.Text = detail;
            if (forceDraw) RenderingServer.ForceDraw();
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to update bar: {e}");
        }
    }

    // ================================================================ 自注入

    private const string AutoloadName = "LoadingBarBoot";
    private const string GdUserPath = "user://loadingbar_boot.gd";
    private const string CfgMarker = "; LoadingBar mod autoload";

    private static void EnsureBootSplashInstalled()
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

    private static void HandoffFromBootSplash()
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
                        "prelude(引擎启动+工坊读取)", Api.LoadPhase.Prelude,
                        0, nowMsec - Recorder.BootAnchorMsec, ""));
                }
            }
            boot.Call("takeover");
            Log.Warn("[ItsLoading] boot splash handed over to mod bar");
        }
        else
        {
            Log.Warn("[ItsLoading] no boot splash found (first run after install)");
        }
    }

    /// <summary>GDScript 启动画面源码。BOOT_VERSION = 7(转义修复版):内容哈希门控自注入。</summary>
    private const string BootSplashGd = @"extends Node
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

func _ready() -> void:
	boot_start_msec = Time.get_ticks_msec()
	if _detect_state() != ""ok"":
		_done = true
		_cleanup_pending = true
		print(""[LoadingBarBoot] mod disabled or unsubscribed — bar suppressed, cleanup deferred"")
	else:
		_build_ui()
		_skip_log_history()
		print(""[LoadingBarBoot] splash ready at frame "", Engine.get_frames_drawn())

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

func _mod_files_present() -> bool:
	var exe_dir := OS.get_executable_path().get_base_dir()
	if FileAccess.file_exists(exe_dir.path_join(""mods/"" + MOD_ID + ""/"" + MOD_ID + "".json"")):
		return true
	var d := exe_dir
	for i in range(5):
		d = d.get_base_dir()
	var ws_root := d.path_join(""workshop/content/2868840"")
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
	_layer.layer = 999
	add_child(_layer)

	var strip := Control.new()
	strip.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	strip.offset_top = -64.0
	_layer.add_child(strip)

	_step = Label.new()
	_step.position = Vector2(24, 8)
	_step.add_theme_font_size_override(""font_size"", 20)
	_step.add_theme_color_override(""font_color"", Color.WHITE)
	_step.text = ""正在启动""
	strip.add_child(_step)

	_detail = Label.new()
	_detail.position = Vector2(24, 36)
	_detail.add_theme_font_size_override(""font_size"", 14)
	_detail.add_theme_color_override(""font_color"", Color(0.85, 0.86, 0.9))
	_detail.text = ""engine boot""
	strip.add_child(_detail)

	_track_w = vs.x - 48.0
	var track := ColorRect.new()
	track.position = Vector2(24, 56)
	track.size = Vector2(_track_w, 5)
	track.color = Color(1, 1, 1, 0.25)
	strip.add_child(track)

	_fill = ColorRect.new()
	_fill.position = Vector2.ZERO
	_fill.size = Vector2(0, 5)
	_fill.color = Color(0.2, 0.85, 0.9)
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
		_step.text = ""创意工坊读取 %d/%d"" % [n, total]
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
		var dll_id := line.get_file().replace("".dll"", """")
		_step.text = ""模组加载""
		_detail.text = dll_id

func _extract_item_id(line: String) -> String:
	var parts := line.split("" "")
	for i in parts.size():
		if parts[i] == ""mod"" and i + 1 < parts.size():
			return parts[i + 1]
	return """"

func _count_workshop() -> int:
	var d := OS.get_executable_path().get_base_dir()
	for i in range(5):
		d = d.get_base_dir()
	var dir := DirAccess.open(d.path_join(""workshop/content/2868840""))
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

    /// <summary>
    /// 耗时测量依赖"我们在其他 mod 之前加载"(补丁装上后才能观测后续加载)。
    /// 新安装/改名后 mod_list 没有我们 → 排序沉底,只能观测到尾部。
    /// 游戏 Initialize 结尾会按 _mods 顺序重建 mod_list,且优雅退出时由游戏
    /// 自行保存设置——因此只做内存重排,绝不自己写用户的 settings.save。
    /// (若首装后的第一次启动被强退,下次仍不完整,再下次自愈;可接受。)
    /// </summary>
    private static void EnsureFirstInLoadOrder()
    {
        int loadedBeforeUs = ModManager.GetLoadedMods().Count();
        var mods = AccessTools.Field(typeof(ModManager), "_mods").GetValue(null)
            as System.Collections.Generic.List<Mod>;
        if (mods == null)
        {
            Log.Warn("[ItsLoading] _mods not accessible — load order left as-is");
            return;
        }
        int idx = mods.FindIndex(m => m.manifest?.id == "ItsLoading");
        if (idx < 0) return;
        if (idx == 0)
        {
            if (loadedBeforeUs > 1)
            {
                Log.Warn($"[ItsLoading] first in list but {loadedBeforeUs - 1} mods loaded before us?");
            }
            return;
        }
        var me = mods[idx];
        mods.RemoveAt(idx);
        mods.Insert(0, me);
        Log.Warn($"[ItsLoading] moved self to load order #0 (was #{idx + 1}, " +
                 $"{loadedBeforeUs} mods loaded before us) — full timing coverage from next boot");
    }

    // ---------------------------------------------------------------- 瀑布图入口(ModConfig 软依赖)
    //
    // 不再自制设置界面按钮(原生 Duplicate 方案与场景唯一名系统冲突,见 v0.9.x 教训)。
    // 改为在 ModConfig 加载完成时(TryLoadMod postfix 天然逐 mod 触发)反射注册其
    // 配置条目:零编译期依赖、manifest 零改动、ModConfig 缺失时静默跳过。

    private static CanvasLayer _waterfallLayer;

    private static void RegisterWaterfallInModConfig(Mod mod)
    {
        var asm = mod.assembly;
        if (asm == null)
        {
            Log.Warn("[ItsLoading] ModConfig assembly unavailable — waterfall entry skipped");
            return;
        }
        var api = asm.GetType("ModConfig.ModConfigApi");
        var entryType = asm.GetType("ModConfig.ConfigEntry");
        var configEnum = asm.GetType("ModConfig.ConfigType");
        if (api == null || entryType == null || configEnum == null)
        {
            Log.Warn("[ItsLoading] ModConfig API types not found — waterfall entry skipped");
            return;
        }

        object entry = System.Activator.CreateInstance(entryType)!;
        void Set(string prop, object value) =>
            entryType.GetProperty(prop)?.SetValue(entry, value);
        Set("Key", "waterfall");
        Set("Label", "启动瀑布图");
        Set("Description", "打开本次启动的耗时瀑布图(各阶段/各 mod 真实时长)");
        Set("Type", System.Enum.Parse(configEnum, "Button"));
        Set("ButtonText", "查看");
        Set("OnChanged", new Action<object>(_ => ShowWaterfall()));

        var entries = System.Array.CreateInstance(entryType, 1);
        entries.SetValue(entry, 0);
        api.GetMethod("Register", new[]
        {
            typeof(string), typeof(string), entries.GetType(),
        })?.Invoke(null, new object[] { "ItsLoading", "不再干等 · It's Loading", entries });
        Log.Warn("[ItsLoading] waterfall entry registered in ModConfig");
    }

    private static void ShowWaterfall()
    {
        Run("show waterfall", () =>
        {
            // 再按一次 = 关闭(toggle)
            if (_waterfallLayer != null)
            {
                _waterfallLayer.QueueFree();
                _waterfallLayer = null;
                return;
            }
            var tree = (SceneTree)Engine.GetMainLoop();
            Vector2 vs = tree.Root.GetVisibleRect().Size;

            _waterfallLayer = new CanvasLayer { Layer = 1200 };

            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.92f) };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dim.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed)
                {
                    _waterfallLayer?.QueueFree();
                    _waterfallLayer = null;
                }
            };
            _waterfallLayer.AddChild(dim);

            var title = new Label
            {
                Text = Api.LoadingDurations.IsReady
                    ? $"启动瀑布图 · 总计 {Api.LoadingDurations.TotalBootMs / 1000.0:F1}s"
                    : "启动瀑布图 · 数据未就绪(完整启动一次后可用)",
            };
            title.Position = new Vector2(48f, 24f);
            title.AddThemeFontSizeOverride("font_size", 24);
            _waterfallLayer.AddChild(title);

            var close = new Button { Text = "✕ 关闭" };
            close.Position = new Vector2(vs.X - 180f, 24f);
            close.Pressed += () => Run("close waterfall", () =>
            {
                _waterfallLayer?.QueueFree();
                _waterfallLayer = null;
            });
            _waterfallLayer.AddChild(close);

            if (Api.LoadingDurations.IsReady)
            {
                BuildWaterfallChart(vs);
            }

            tree.Root.AddChild(_waterfallLayer);
            Log.Info("[ItsLoading] waterfall opened");
        });
    }

    private static Color WfColor(Api.LoadPhase p) => p switch
    {
        Api.LoadPhase.Prelude => new Color(0.55f, 0.57f, 0.62f, 1f),
        Api.LoadPhase.ModLoad => new Color(0.20f, 0.85f, 0.90f, 1f),
        Api.LoadPhase.BootStep => new Color(0.95f, 0.70f, 0.25f, 1f),
        Api.LoadPhase.AssetSession => new Color(0.40f, 0.85f, 0.50f, 1f),
        _ => Colors.White,
    };

    private static void BuildWaterfallChart(Vector2 vs)
    {
        double total = Math.Max(1.0, Api.LoadingDurations.TotalBootMs);

        // 汇总所有 span,按时间轴排序
        var rows = new System.Collections.Generic.List<Api.LoadSpan>();
        rows.AddRange(Api.LoadingDurations.Phases);
        rows.AddRange(Api.LoadingDurations.BootSteps);
        rows.AddRange(Api.LoadingDurations.AssetSessions);
        rows.AddRange(Api.LoadingDurations.ModLoads);
        rows.Sort((a, b) => a.StartMs != b.StartMs
            ? a.StartMs.CompareTo(b.StartMs)
            : b.DurationMs.CompareTo(a.DurationMs));

        // 滚动区(菜单阶段正常帧循环,Container 可用)
        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.OffsetTop = 80f;
        scroll.OffsetBottom = -40f;
        scroll.OffsetLeft = 48f;
        scroll.OffsetRight = -48f;
        _waterfallLayer.AddChild(scroll);

        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        scroll.AddChild(box);

        // 时间轴刻度(每 5s)
        var ruler = new Control
        {
            CustomMinimumSize = new Vector2(0, 22f),
            SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
        };
        box.AddChild(ruler);
        for (double t = 0; t <= total; t += 5000.0)
        {
            float frac = (float)(t / total);
            var tick = new Label { Text = $"{t / 1000.0:F0}s" };
            tick.AnchorLeft = frac;
            tick.AddThemeFontSizeOverride("font_size", 12);
            tick.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.45f));
            ruler.AddChild(tick);
            var line = new ColorRect { Color = new Color(1f, 1f, 1f, 0.08f) };
            line.AnchorLeft = frac;
            line.AnchorRight = frac;
            line.OffsetTop = 20f;
            line.OffsetBottom = 2000f;
            ruler.AddChild(line);
        }

        foreach (var s in rows)
        {
            var row = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(0, 18f),
                SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            };
            box.AddChild(row);

            var name = new Label
            {
                Text = $"{s.Id}  {s.DurationMs / 1000.0:F2}s",
                CustomMinimumSize = new Vector2(340f, 18f),
                ClipText = true,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            name.AddThemeColorOverride("font_color", WfColor(s.Phase));
            row.AddChild(name);

            var barArea = new Control
            {
                CustomMinimumSize = new Vector2(0, 18f),
                SizeFlagsHorizontal = (Control.SizeFlags.Fill | Control.SizeFlags.Expand),
            };
            row.AddChild(barArea);

            float start = (float)(s.StartMs / total);
            float end = (float)Math.Min(1.0, (s.StartMs + s.DurationMs) / total);
            var bar = new ColorRect { Color = WfColor(s.Phase) };
            bar.AnchorLeft = start;
            bar.AnchorRight = Math.Max(end, start + 0.0015f);
            bar.AnchorTop = 0f;
            bar.AnchorBottom = 1f;
            bar.OffsetTop = 4f;
            bar.OffsetBottom = -4f;
            barArea.AddChild(bar);
        }
    }

    // ================================================================ Harmony patches

    private static void PatchLoader()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading");
        var original = AccessTools.Method(typeof(ModManager), "TryLoadMod");
        if (original == null)
        {
            Log.Error("[ItsLoading] TryLoadMod not found — game update changed signature?");
            return;
        }
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeTryLoadMod)),
            postfix: new HarmonyMethod(typeof(ItsLoading), nameof(AfterModLoad)));
        Log.Warn("[ItsLoading] TryLoadMod prefix+postfix installed OK");
    }

    private static void BeforeTryLoadMod(Mod mod)
    {
        // 单次时钟读;mod 段总区间起点
        Recorder.PrefixCalls++;
        Recorder.ModStartTicks = Sw.ElapsedTicks;
        if (Recorder.FirstModTicks < 0) Recorder.FirstModTicks = Recorder.ModStartTicks;
    }

    private static void AfterModLoad(Mod mod)
    {
        long now = Sw.ElapsedMilliseconds;
        long delta = now - _lastMs;
        _lastMs = now;
        _count++;
        string id = mod.manifest?.id ?? "<null>";

        Log.Warn($"[ItsLoading] [{_count}/{_total}] {id} -> {mod.state} " +
                 $"+{delta}ms frame={Engine.GetFramesDrawn()}");

        // 耗时记录(prefix→postfix 真实区间,亚毫秒精度)
        long nowTicks = Sw.ElapsedTicks;
        Recorder.LastModTicks = nowTicks;
        Recorder.ModSpans.Add(new Api.LoadSpan(
            id, Api.LoadPhase.ModLoad,
            Recorder.ToEngineMs(Recorder.ModStartTicks),
            (nowTicks - Recorder.ModStartTicks) * Recorder.SwTicksToMs,
            mod.state.ToString()));

        // ModConfig 加载完成的瞬间反射注册瀑布图入口(软依赖:缺失时静默跳过)
        if (id == "ModConfig" && mod.state == ModLoadState.Loaded)
        {
            Run("register in ModConfig", () => RegisterWaterfallInModConfig(mod));
        }

        float frac = 0.25f + 0.35f * (_count / (float)_total);
        SetProgress(frac, $"模组加载 {_count}/{_total}", $"{id} · +{delta}ms");

        if (_count >= _total && !_done)
        {
            _done = true;
            Recorder.PhaseSpans.Add(new Api.LoadSpan(
                "mod_load", Api.LoadPhase.ModLoad,
                Recorder.ToEngineMs(Recorder.FirstModTicks),
                (Recorder.LastModTicks - Recorder.FirstModTicks) * Recorder.SwTicksToMs,
                $"{_count} mods"));
            SetProgress(0.60f, "模组加载完成", $"共 {_count} 个 · {Sw.ElapsedMilliseconds}ms");
            Log.Warn($"[ItsLoading] all mods processed @ +{Sw.ElapsedMilliseconds}ms");
        }
    }

    // ---- 启动子步骤(Essential 同步长黑屏期间的 checkpoints) ----

    private static readonly (string Type, string Method, string Label, float Progress)[] Steps =
    {
        ("MegaCrit.Sts2.Core.Assets.AtlasManager", "LoadEssentialAtlases", "图集加载", 0.615f),
        ("MegaCrit.Sts2.Core.Localization.LocManager", "Initialize", "本地化初始化", 0.625f),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Init", "模型数据库构建", 0.635f),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "InitIds", "模型 ID 注册", 0.645f),
        ("MegaCrit.Sts2.Core.Models.ModelDb", "Preload", "模型资源预载", 0.655f),
    };

    private static readonly System.Collections.Generic.Dictionary<MethodBase, (string Label, float Progress)> StepMap =
        new();

    // ---- 资产会话实时进度(NAssetLoader._Process 主线程每帧泵 AssetLoadingSession.Process) ----

    /// <summary>启动期的会话名 → 单条进度刻度上的子区间。游戏内会话(房间/角色)不在此表,自动忽略。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, (float Start, float End)> SessionRanges =
        new()
        {
            { "IntroLogo", (0.66f, 0.70f) },
            { "MainMenuEssentials", (0.70f, 0.82f) },
            { "MainMenu", (0.88f, 1.00f) },
        };

    // ConditionalWeakTable:session 结束后可被 GC,不阻止回收;值对象复用,无 per-frame 分配
    private sealed class SessionStat
    {
        public int Total;
        public long FirstTicks, LastTicks;
        public bool Recorded;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SessionStat> SessionStats = new();

    private static void PatchBootPhases()
    {
        var harmony = new Harmony("com.somiona.sts2.itsloading.boot");
        foreach (var (type, method, label, progress) in Steps)
        {
            var mi = AccessTools.Method(AccessTools.TypeByName(type), method);
            if (mi == null)
            {
                Log.Warn($"[ItsLoading] step not found, skipped: {type}.{method}");
                continue;
            }
            StepMap[mi] = (label, progress);
            harmony.Patch(mi, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(StepPrefix)));
        }
        Log.Warn($"[ItsLoading] step patches installed ({StepMap.Count}/{Steps.Length})");

        var menu = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:LaunchMainMenu");
        if (menu != null)
        {
            harmony.Patch(menu, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeMainMenu)));
            Log.Warn("[ItsLoading] LaunchMainMenu patch installed");
        }

        // 资产会话:每帧真实进度(主线程 _Process 泵,帧自然流动,无需 ForceDraw)
        var sessionProcess = AccessTools.Method(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.AssetLoadingSession"), "Process");
        if (sessionProcess != null)
        {
            harmony.Patch(sessionProcess,
                postfix: new HarmonyMethod(typeof(ItsLoading), nameof(AfterSessionProcess)));
            Log.Warn("[ItsLoading] AssetLoadingSession.Process patch installed");
        }

        var logoPlay = AccessTools.Method(
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NLogoAnimation:PlayAnimation");
        if (logoPlay != null)
        {
            harmony.Patch(logoPlay, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeLogoPlay)));
            Log.Warn("[ItsLoading] PlayAnimation patch installed");
        }

        var loadMenu = AccessTools.Method("MegaCrit.Sts2.Core.Nodes.NGame:LoadMainMenu");
        if (loadMenu != null)
        {
            harmony.Patch(loadMenu, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeLoadMenu)));
            Log.Warn("[ItsLoading] LoadMainMenu patch installed");
        }

        var deferred = AccessTools.Method("MegaCrit.Sts2.Core.Helpers.OneTimeInitialization:ExecuteDeferred");
        if (deferred != null)
        {
            harmony.Patch(deferred, prefix: new HarmonyMethod(typeof(ItsLoading), nameof(BeforeDeferred)));
            Log.Warn("[ItsLoading] ExecuteDeferred patch installed");
        }
    }

    // ---- 资产会话 postfix:反射读队列实时状态 ----

    private static System.Reflection.FieldInfo _fName, _fToLoad, _fLoading, _fFinalizing, _fVfx, _fVfxLoading, _fTotal;

    private static void CacheSessionFields(Type t)
    {
        _fName = AccessTools.Field(t, "_name");
        _fToLoad = AccessTools.Field(t, "_toLoad");
        _fLoading = AccessTools.Field(t, "_loading");
        _fFinalizing = AccessTools.Field(t, "_finalizing");
        _fVfx = AccessTools.Field(t, "_vfxScenes");
        _fVfxLoading = AccessTools.Field(t, "_vfxLoading");
        _fTotal = AccessTools.Field(t, "_totalLoaded");
    }

    private static int Count(object queue) => (queue as System.Collections.ICollection)?.Count ?? 0;

    private static void AfterSessionProcess(object __instance)
    {
        try
        {
            if (_fName == null) CacheSessionFields(__instance.GetType());

            string name = _fName?.GetValue(__instance) as string ?? "";
            if (!SessionRanges.TryGetValue(name, out var range)) return;

            if (!UiOk || _barDead) return;

            int remaining = Count(_fToLoad?.GetValue(__instance))
                          + Count(_fLoading?.GetValue(__instance))
                          + Count(_fFinalizing?.GetValue(__instance))
                          + Count(_fVfx?.GetValue(__instance))
                          + ((_fVfxLoading?.GetValue(__instance) is true) ? 1 : 0);
            int loaded = _fTotal?.GetValue(__instance) as int? ?? 0;
            long nowTicks = Sw.ElapsedTicks;

            // 会话统计:首见记总数与起点,每帧只更新 LastTicks(一次字段写,零分配)
            SessionStat stat = SessionStats.GetValue(__instance, _ => new SessionStat());
            if (stat.Total <= 0)
            {
                stat.Total = loaded + remaining;
                stat.FirstTicks = nowTicks;
            }
            stat.LastTicks = nowTicks;
            if (stat.Total > 0 && remaining == 0 && !stat.Recorded)
            {
                stat.Recorded = true;
                Recorder.SessionSpans.Add(new Api.LoadSpan(
                    name, Api.LoadPhase.AssetSession,
                    Recorder.ToEngineMs(stat.FirstTicks),
                    (stat.LastTicks - stat.FirstTicks) * Recorder.SwTicksToMs,
                    $"{loaded}/{stat.Total}"));
            }

            float local = 1f - remaining / (float)stat.Total;
            float frac = range.Start + (range.End - range.Start) * local;
            SetProgress(frac, $"资产加载 · {name}", $"{loaded}/{stat.Total} 个资源", forceDraw: false);
        }
        catch (Exception e)
        {
            Log.Error($"[ItsLoading] FAILED to read session state: {e}");
        }
    }

    private static void BeforeLogoPlay()
    {
        SetProgress(0.82f, "播放开场动画", "可跳过(若已开启跳过则无此阶段)");
    }

    private static void BeforeLoadMenu()
    {
        SetProgress(0.88f, "加载主菜单", "");
    }

    private static void StepPrefix(MethodBase __originalMethod)
    {
        if (!StepMap.TryGetValue(__originalMethod, out var s)) return;
        // 步骤级耗时:本步骤起点到下一检查点(相邻差分,无额外采样)
        long nowTicks = Sw.ElapsedTicks;
        if (Recorder.StepSpans.Count > 0 && Recorder.LastStepTicks >= 0)
        {
            var prev = Recorder.StepSpans[^1];
            Recorder.StepSpans[^1] = prev with
            {
                DurationMs = (nowTicks - Recorder.LastStepTicks) * Recorder.SwTicksToMs,
            };
        }
        Recorder.LastStepTicks = nowTicks;
        Recorder.StepSpans.Add(new Api.LoadSpan(
            s.Label, Api.LoadPhase.BootStep, Recorder.ToEngineMs(nowTicks), 0, ""));
        SetProgress(s.Progress, s.Label, $"启动步骤 · +{Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>logo/云同步后的启动收尾(LaunchMainMenu 调用点已逆向确认)。只处理一次。</summary>
    private static void BeforeMainMenu()
    {
        if (_menuHandled) return;
        _menuHandled = true;
        SetProgress(0.66f, "启动开场", $"云同步+存档读取完成 · +{Sw.ElapsedMilliseconds}ms");
    }

    /// <summary>主菜单已显示(ExecuteDeferred 语义):收尾最后一个步骤 span,冻结数据,移除 UI。</summary>
    private static void BeforeDeferred()
    {
        // 关闭最后一个挂起的启动步骤 span
        if (Recorder.StepSpans.Count > 0 && Recorder.LastStepTicks >= 0)
        {
            long nowTicks = Sw.ElapsedTicks;
            var prev = Recorder.StepSpans[^1];
            Recorder.StepSpans[^1] = prev with
            {
                DurationMs = (nowTicks - Recorder.LastStepTicks) * Recorder.SwTicksToMs,
            };
        }

        if (Recorder.BootAnchorMsec >= 0)
        {
            Recorder.TotalBootMs = (long)Time.GetTicksMsec() - Recorder.BootAnchorMsec;
        }
        Api.LoadingDurations.Freeze();

        // 一行启动摘要(诊断用,常量级日志)
        if (Recorder.ModSpans.Count > 0)
        {
            var top = new System.Text.StringBuilder("[ItsLoading] boot ");
            top.Append(Recorder.TotalBootMs.ToString("F0")).Append("ms")
               .Append($" (prefix={Recorder.PrefixCalls} postfix={Recorder.ModSpans.Count})")
               .Append("; slowest mods:");
            foreach (var s in Recorder.ModSpans
                         .OrderByDescending(m => m.DurationMs)
                         .ThenBy(m => m.Id, StringComparer.Ordinal)
                         .Take(3))
            {
                top.Append(' ').Append(s.Id).Append('=').Append(s.DurationMs.ToString("F0")).Append("ms");
            }
            Log.Info(top.ToString());
        }

        SetProgress(1.0f, "启动完成", $"{Sw.ElapsedMilliseconds}ms");
        var tree = (SceneTree)Engine.GetMainLoop();
        var timer = tree.CreateTimer(2.0);
        timer.Timeout += () => Run("remove bar", () =>
        {
            _barDead = true;
            _layer?.QueueFree();
        });
    }
}
