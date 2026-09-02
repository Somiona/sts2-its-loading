#!/bin/bash
# 安装到本地游戏(目标优先级:GAME_DIR > .env 的 STS2_GAME_DIR > macOS 原生版默认)
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] && source .env
GAME_DIR="${GAME_DIR:-${STS2_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}}"
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
# 素材版权声明(狐狸 LGPL-2.1 出处 + logo AI 生成说明)
cp LICENSE_CLAIM.md "$GAME_MODS/"
# 加载屏呈现层与主题数据(仓库 src/ItsLoading/{render,themes} → 发行同名目录)。
# render 发行只带 *.gd —— C# 桥/呈现面编译进 dll,不随数据发行;
# C# Install 会把两棵树差异刷新到 user://itsloading/,override.cfg 指向那里
# render/themes 是源树的完全派生物 —— 先清再拷(cp 不会删旧文件,残留会
# 经镜像同步进入 user://,如已退役的 theme.gd)
rm -rf "$GAME_MODS/render" "$GAME_MODS/themes"
mkdir -p "$GAME_MODS/render" "$GAME_MODS/themes"
cp src/ItsLoading/render/*.gd "$GAME_MODS/render/"
cp -R src/ItsLoading/themes/. "$GAME_MODS/themes/"
# v0.20 前的平铺布局(mod 根的 gd/ 目录)不再被读取,本地升级时清掉
rm -rf "$GAME_MODS/gd"
# 只松散复制各语言的 strings.json(gd/C# 运行时读磁盘);settings_ui.json 仅走 pck,
# 松散放置会被游戏 mod 扫描器当 manifest 报错。语言列表自动发现。
mkdir -p "$GAME_MODS/localization"
for f in src/ItsLoading/localization/*/strings.json; do
  lang_dir="${f%/strings.json}"
  lang="${lang_dir##*/}"
  mkdir -p "$GAME_MODS/localization/$lang"
  cp "$f" "$GAME_MODS/localization/$lang/"
done
echo "installed to: $GAME_MODS"
ls -la "$GAME_MODS"
