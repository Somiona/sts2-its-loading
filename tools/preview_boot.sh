#!/bin/bash
# 离屏预览全部主题(不启动游戏)。
#   1) 假 mod 布局 = 仓库 render/*.gd + themes/<id>/ + manifest + 各语言 strings.json
#   2) 预览工程:驱动(preview_driver.gd)把 gd 树镜像到 user://itsloading(复刻
#      C# Install 的同步),mod 目录经 ITSLOADING_PREVIEW_MOD_DIR 环境变量注入
#      boot.gd —— 不改产品代码即可跑完整装载链
#   3) 逐主题跑时间线剧本:csharp_present 各阶段/平滑/不定/takeover 淡出,
#      截图 + 确定性读数(fill/track、pos.x、scale、mask 段)+ 越轨断言
# 用法:tools/preview_boot.sh
# 输出:$PREVIEW_DIR/shots/<theme>/0N_*.png + [readback]/[geom] 行;断言失败退出码 1
# 配置(GODOT_BIN / PREVIEW_DIR)见 .env.example。
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .env ] && source .env

PREVIEW_DIR="${PREVIEW_DIR:-/tmp/itsloading_preview}"
rm -rf "$PREVIEW_DIR"
mkdir -p "$PREVIEW_DIR/fake_exe/mods/ItsLoading/render" "$PREVIEW_DIR/fake_exe/mods/ItsLoading/themes" "$PREVIEW_DIR/shots"

# Godot 可执行文件:.env 的 GODOT_BIN > /Applications 探测(与 check_gd_template.py 同一回退顺序)
if [[ -z "${GODOT_BIN:-}" ]]; then
  GODOT_BIN=$(ls /Applications/Godot*.app/Contents/MacOS/Godot 2>/dev/null | head -1 || true)
fi
[[ -n "$GODOT_BIN" && -x "$GODOT_BIN" ]] || { echo "!! 未找到 Godot,请在 .env 设 GODOT_BIN" >&2; exit 1; }

# 1) 假 mod 布局(与 install.sh 相同的布局;主题整树)
cp src/ItsLoading/render/*.gd "$PREVIEW_DIR/fake_exe/mods/ItsLoading/render/"
cp -R src/ItsLoading/themes/. "$PREVIEW_DIR/fake_exe/mods/ItsLoading/themes/"
cp src/ItsLoading/ItsLoading.json "$PREVIEW_DIR/fake_exe/mods/ItsLoading/"
for f in src/ItsLoading/localization/*/strings.json; do
  lang_dir="${f%/strings.json}"
  lang="${lang_dir##*/}"
  mkdir -p "$PREVIEW_DIR/fake_exe/mods/ItsLoading/localization/$lang"
  cp "$f" "$PREVIEW_DIR/fake_exe/mods/ItsLoading/localization/$lang/"
done

# 2) 预览工程(驱动脚本是仓库里的静态文件)
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
cp tools/preview_driver.gd "$PREVIEW_DIR/preview.gd"

# 3) 跑(会闪一个窗口,三主题一轮后自动退出)
export ITSLOADING_PREVIEW_MOD_DIR="$PREVIEW_DIR/fake_exe/mods/ItsLoading"
export PREVIEW_SHOTS="$PREVIEW_DIR/shots"
echo "==> preview: dir=$PREVIEW_DIR"
if ! "$GODOT_BIN" --path "$PREVIEW_DIR" > "$PREVIEW_DIR/run.log" 2>&1; then
  grep -E "\[readback\]|\[geom\]|\[preview\]|LoadingBarBoot|SCRIPT ERROR" "$PREVIEW_DIR/run.log" || true
  echo "!! preview 断言失败(详见 $PREVIEW_DIR/run.log)" >&2
  exit 1
fi
grep -E "\[readback\]|\[geom\]|\[preview\]|LoadingBarBoot|SCRIPT ERROR" "$PREVIEW_DIR/run.log"
echo "shots: $PREVIEW_DIR/shots/<theme>/*.png"
