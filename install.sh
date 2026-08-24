#!/bin/bash
# 安装到本地游戏(默认 macOS 原生版路径,可用 GAME_DIR 覆盖)
set -euo pipefail
cd "$(dirname "$0")"

GAME_DIR="${GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
GAME_MODS="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/ItsLoading"

mkdir -p "$GAME_MODS"
cp src/ItsLoading/bin/Release/net9.0/ItsLoading.dll "$GAME_MODS/"
cp src/ItsLoading/ItsLoading.json "$GAME_MODS/"
cp src/ItsLoading/mod_image.png "$GAME_MODS/"
echo "installed to: $GAME_MODS"
ls -la "$GAME_MODS"
