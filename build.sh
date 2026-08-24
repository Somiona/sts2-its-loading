#!/bin/bash
# 构建 It's Loading(不再干等)
# 依赖的引用 dll 会在缺失时自动从本地游戏安装复制(不会入库)
set -euo pipefail
cd "$(dirname "$0")"

GAME_DIR="${GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
LIBS="src/ItsLoading/libs"

if [[ "$(uname -m)" == "arm64" ]]; then
  DATA="data_sts2_macos_arm64"
else
  DATA="data_sts2_macos_x86_64"
fi

if [[ ! -f "$LIBS/sts2.dll" ]]; then
  SRC="$GAME_DIR/SlayTheSpire2.app/Contents/Resources/$DATA"
  echo "fetching reference dlls from: $SRC"
  mkdir -p "$LIBS"
  cp "$SRC/sts2.dll" "$SRC/0Harmony.dll" "$SRC/GodotSharp.dll" "$LIBS/"
fi

dotnet build src/ItsLoading/ItsLoading.csproj -c Release

OUT="src/ItsLoading/bin/Release/net9.0"
if [[ -f src/ItsLoading/mod_image.png ]]; then
  # 打包图标 pck(v3 格式,游戏的 Godot 4.5.1 fork 实测可加载;勿用 Godot 4.7 的 PCKPacker,它写 v4 会被拒)
  python3 tools/build_pck.py "$OUT/ItsLoading.pck" \
    "ItsLoading/mod_image.png" src/ItsLoading/mod_image.png
else
  echo "!! 缺少 src/ItsLoading/mod_image.png(400x400),跳过 pck;游戏内将不显示图标" >&2
fi
echo
echo "dll => $OUT/ItsLoading.dll"
