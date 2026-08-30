#!/bin/bash
# 离屏预览 gd 启动画面(不启动游戏):
#   1) 反射抠出真实生成脚本(BootSplash.BootSplashGd = token 替换完的产物,零真值表漂移)
#   2) stub 掉 OS exe 探测,指向假 mod 布局(manifest + 全主题素材 + localization)
#   3) Godot 跑预览工程:驱动按时间线调 csharp_present 各阶段/takeover,逐步截图
# 用法:先 ./build.sh(抠的是构建产物),再 tools/preview_boot.sh。
# 输出:$PREVIEW_DIR/shots/*.png + 终端 [readback] 确定性读数(放大枚位/遮罩百分比)。
# 配置(GODOT_BIN / STS2_GAME_DIR / PREVIEW_DIR / PREVIEW_THEME)见 .env.example。
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .env ] && source .env

STS2_GAME_DIR="${STS2_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
PREVIEW_DIR="${PREVIEW_DIR:-/tmp/itsloading_preview}"
PREVIEW_THEME="${PREVIEW_THEME:-slaytheshin}"

# Godot 可执行文件:.env 的 GODOT_BIN > /Applications 探测(与 check_gd_template.py 同回退)
if [[ -z "${GODOT_BIN:-}" ]]; then
  GODOT_BIN=$(ls /Applications/Godot*.app/Contents/MacOS/Godot 2>/dev/null | head -1 || true)
fi
[[ -n "$GODOT_BIN" && -x "$GODOT_BIN" ]] || { echo "!! 未找到 Godot,请在 .env 设 GODOT_BIN" >&2; exit 1; }

DLL="src/ItsLoading/bin/Release/net9.0/ItsLoading.dll"
[[ -f "$DLL" ]] || { echo "!! 缺 $DLL,先 ./build.sh" >&2; exit 1; }
DLL_ABS="$(cd "$(dirname "$DLL")" && pwd)/$(basename "$DLL")"

# GodotSharp.dll 从本机游戏安装解析(反射加载 ItsLoading.dll 需要它可寻址)
GODOTSHARP=$(find "$STS2_GAME_DIR" -name GodotSharp.dll 2>/dev/null | head -1)
[[ -n "$GODOTSHARP" ]] || { echo "!! 未在 STS2_GAME_DIR 下找到 GodotSharp.dll" >&2; exit 1; }

mkdir -p "$PREVIEW_DIR/extract" "$PREVIEW_DIR/shots" "$PREVIEW_DIR/fake_exe/mods/ItsLoading"

# 1) 反射抠脚本的小工具(每次重生成 csproj:两个 HintPath 随本机布局变)
cat > "$PREVIEW_DIR/extract/Program.cs" <<'EOF'
using System;
using System.IO;
using System.Reflection;

var asm = Assembly.LoadFrom(args[0]);
var t = asm.GetType("ItsLoading.BootSplash") ?? throw new Exception("type not found");
var f = t.GetField("BootSplashGd", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("field not found");
File.WriteAllText(args[1], (string)f.GetValue(null)!);
EOF
cat > "$PREVIEW_DIR/extract/extract.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>extract</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="ItsLoading"><HintPath>$DLL_ABS</HintPath></Reference>
    <Reference Include="GodotSharp"><HintPath>$GODOTSHARP</HintPath></Reference>
  </ItemGroup>
</Project>
EOF
(cd "$PREVIEW_DIR/extract" && dotnet run -- "$DLL_ABS" "$PREVIEW_DIR/boot_raw.gd" >/dev/null)

# 2) stub exe 探测 → 假 mod 目录(_mod_dir/_workshop_root 都吃这个基目录)
python3 - "$PREVIEW_DIR" <<'EOF'
import sys
d = sys.argv[1]
src = open(f"{d}/boot_raw.gd").read()
assert "OS.get_executable_path().get_base_dir()" in src
open(f"{d}/boot_stub.gd", "w").write(
    src.replace("OS.get_executable_path().get_base_dir()", f'"{d}/fake_exe"'))
EOF

# 3) 假 mod 布局:manifest + 全主题素材(扁平整名,与 install.sh 语义一致)+ 各语言 strings.json
cp src/ItsLoading/ItsLoading.json "$PREVIEW_DIR/fake_exe/mods/ItsLoading/"
cp src/ItsLoading/Themes/*/*.png "$PREVIEW_DIR/fake_exe/mods/ItsLoading/"
for f in src/ItsLoading/localization/*/strings.json; do
  lang_dir="${f%/strings.json}"; lang="${lang_dir##*/}"
  mkdir -p "$PREVIEW_DIR/fake_exe/mods/ItsLoading/localization/$lang"
  cp "$f" "$PREVIEW_DIR/fake_exe/mods/ItsLoading/localization/$lang/"
done

# 4) 预览工程:主题 cfg 由驱动在注入前写入 user://(走真实 _read_theme 读链)
cat > "$PREVIEW_DIR/project.godot" <<'EOF'
config_version=5

[application]

config/name="ItsLoadingPreview"
run/main_scene="res://preview.tscn"
config/features=PackedStringArray("4.7")

[display]

window/size/viewport_width=1280
window/size/viewport_height=720

[rendering]

renderer/rendering_method="gl_compatibility"
renderer/rendering_method.mobile="gl_compatibility"
EOF
cat > "$PREVIEW_DIR/preview.tscn" <<'EOF'
[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://preview.gd" id="1"]

[node name="Preview" type="Node"]
script = ExtResource("1")
EOF
cat > "$PREVIEW_DIR/preview.gd" <<'EOF'
extends Node
# 离屏预览驱动:写主题 cfg → 注入真实生成脚本(已 stub exe 探测)→ 时间线驱动 + 截图。
# 注意:主场景 _ready 里往 root add_child 必须 deferred(直接加会因 root 忙被拒、节点不进树)。
var boot: Node

func _ready() -> void:
	DirAccess.make_dir_recursive_absolute("user://mod_configs")
	var f := FileAccess.open("user://mod_configs/ItsLoading.cfg", FileAccess.WRITE)
	f.store_string("{\n  \"Theme\": \"__THEME__\"\n}\n")
	f.close()
	var script: GDScript = load("res://boot_stub.gd")
	boot = script.new()
	get_tree().root.add_child.call_deferred(boot)
	await get_tree().create_timer(1.0).timeout
	_snap("01_boot_ready_stage1")
	_readback("stage1")
	boot.csharp_attach()
	boot.csharp_present(0.30, 0.40, 2, "Loading mods", "ItsLoading.dll +12ms")
	await get_tree().create_timer(0.3).timeout
	_snap("02_stage2_mask40")
	_readback("stage2")
	boot.csharp_present(0.60, -1.0, 3, "Essential data", "")
	await get_tree().create_timer(0.6).timeout
	_snap("03_stage3_indeterminate")
	boot.csharp_present(0.70, 0.10, 4, "Opening assets", "12/120 splash.png")
	boot.csharp_present(0.72, 0.60, 4, "Opening assets", "72/120 title.png")
	await get_tree().create_timer(0.9).timeout
	_snap("04_stage4_smooth")
	_readback("stage4")
	for i in range(20):
		boot.csharp_present(0.80, float(i) / 20.0, 5, "Menu assets", "item_%d.png +3ms" % i)
		await get_tree().create_timer(0.05).timeout
	_snap("05_stage5_log_full")
	_readback("stage5")
	boot.csharp_present(1.0, 1.0, 7, "Main menu", "ready")
	await get_tree().create_timer(0.3).timeout
	_snap("06_stage7_done")
	_readback("stage7")
	boot.takeover()
	await get_tree().create_timer(0.2).timeout
	_snap("07_fade_mid")
	await get_tree().create_timer(1.0).timeout
	get_tree().quit()

func _snap(name: String) -> void:
	var dir := OS.get_environment("PREVIEW_SHOTS")
	var img := get_viewport().get_texture().get_image()
	img.save_png((dir if dir != "" else "user://") + "/%s.png" % name)
	print("[preview] snap ", name)

# 确定性读数(视觉模型数小图标不可靠,断言靠这个):第一排各枚 scale 与视觉顶/底边
# (矩形终身不变,放大走 pivot+scale:底边不动、顶边随 scale 上移)+ 填充段右缘占轨比
func _readback(tag: String) -> void:
	var row: Array = boot.get("_ss_row1")
	var scales := []
	var v_tops := []
	var v_bottoms := []
	for i in row.size():
		var s: float = row[i].scale.x
		var h: float = row[i].size.y
		scales.append(String.num(s, 2))
		v_tops.append(int(round(row[i].position.y + h * (1.0 - s))))
		v_bottoms.append(int(round(row[i].position.y + h)))
	# slaytheshin 读孪生材质的 seg_b(剪贴段右缘);其余主题回退 fill 宽/轨
	var mats: Array = boot.get("_ss_fill_mats")
	var frac: float
	if not mats.is_empty():
		frac = float(mats[0].get_shader_parameter("seg_b"))
	else:
		frac = boot.get("_local_fill").size.x / boot.get("_track_w")
	print("[readback] ", tag, " scale=", scales, " vtop=", v_tops,
			" vbottom=", v_bottoms, " fill/track=", String.num(frac * 100.0, 1), "%")
EOF
sed -i '' "s/__THEME__/$PREVIEW_THEME/" "$PREVIEW_DIR/preview.gd"

# 5) 跑(会闪一个窗口,自动退出)
export PREVIEW_SHOTS="$PREVIEW_DIR/shots"
echo "==> preview: theme=$PREVIEW_THEME dir=$PREVIEW_DIR"
"$GODOT_BIN" --path "$PREVIEW_DIR" 2>&1 | grep -E "readback|LoadingBarBoot|preview"
