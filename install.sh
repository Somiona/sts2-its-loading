#!/bin/bash
# 安装到本地游戏(默认 macOS 原生版路径,可用 GAME_DIR 覆盖)
set -euo pipefail
cd "$(dirname "$0")"

GAME_DIR="${GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
# mods 目录 = 可执行文件旁的 mods(ModManager 语义)。macOS 原生版在 .app 内,
# 其余(Windows 原生、CrossOver/Wine 瓶内)在游戏根目录。
if [[ -d "$GAME_DIR/SlayTheSpire2.app" ]]; then
  GAME_MODS="$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/ItsLoading"
else
  GAME_MODS="$GAME_DIR/mods/ItsLoading"
fi

mkdir -p "$GAME_MODS"
cp src/ItsLoading/bin/Release/net9.0/ItsLoading.dll "$GAME_MODS/"
cp src/ItsLoading/bin/Release/net9.0/ItsLoading.pck "$GAME_MODS/"
cp src/ItsLoadingCompat/bin/Release/net9.0/ItsLoadingCompat.dll "$GAME_MODS/"
cp src/ItsLoading/ItsLoading.json "$GAME_MODS/"
cp src/ItsLoading/mod_image.png "$GAME_MODS/"
# 只松散复制各语言的 strings.json(gd/C# 运行时读磁盘);settings_ui.json 仅走 pck,
# 松散放置会被游戏 mod 扫描器当 manifest 报错(2026-08-28 实测)。语言列表自动化。
mkdir -p "$GAME_MODS/localization"
for f in src/ItsLoading/localization/*/strings.json; do
  lang_dir="${f%/strings.json}"
  lang="${lang_dir##*/}"
  mkdir -p "$GAME_MODS/localization/$lang"
  cp "$f" "$GAME_MODS/localization/$lang/"
done
echo "installed to: $GAME_MODS"
ls -la "$GAME_MODS"
