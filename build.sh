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

# mod 图标由 dll 运行时从 mod 目录直接读取(Image API 认裸 PNG),
# 无需 PCK —— ResourceLoader 在导出版游戏里加载不了未导入的 PNG
echo
echo "dll => src/ItsLoading/bin/Release/net9.0/ItsLoading.dll"
