#!/bin/bash
# 上传/更新 Steam 创意工坊物品(Slay the Spire 2, appid 2868840)
#
# 用法:
#   ./tools/publish_workshop.sh <Steam账号名>          # 构建 + 上传(密码/Steam Guard 交互输入)
#   ./tools/publish_workshop.sh <Steam账号名> --stage-only  # 只构建+暂存,不上传(检查内容用)
#
# 首次上传:tools/workshop_item_id.txt 留空 → Steam 自动分配物品 ID,
#           上传成功后把输出里的 Published File ID 填进该文件(之后即为更新)。
# 前置:brew install steamcmd;浏览器登录过 Steam 并接受过工坊协议。
set -euo pipefail
cd "$(dirname "$0")/.."

APP_ID=2868840
ACCOUNT="${1:?用法: publish_workshop.sh <Steam账号名> [--stage-only]}"
STAGE_ONLY="${2:-}"
if [ "$STAGE_ONLY" != "--stage-only" ] && [ "$ACCOUNT" = "test" ]; then
  echo "!! 账号名是 'test' —— 像占位符,不是真实 Steam 账号。如确要继续,改脚本里的这行保险。" >&2
  exit 1
fi
STAGE="$PWD/dist/workshop"
VDF="$PWD/dist/workshop_item.vdf"
ID_FILE="tools/workshop_item_id.txt"

VERSION=$(grep -oE '<Version>[^<]+' src/ItsLoading/ItsLoading.csproj | head -1 | cut -d'>' -f2)
ITEM_ID=$(grep -Eo '[0-9]+' "$ID_FILE" 2>/dev/null | head -1 || true)

echo "==> 构建(含 i18n 与时间线测试门禁)"
./build.sh

echo "==> 暂存工坊内容 → $STAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE"
OUT=src/ItsLoading/bin/Release/net9.0
cp "$OUT/ItsLoading.dll" "$OUT/ItsLoading.pck" "$STAGE/"
cp src/ItsLoadingCompat/bin/Release/net9.0/ItsLoadingCompat.dll "$STAGE/"
cp src/ItsLoading/ItsLoading.json src/ItsLoading/mod_image.png "$STAGE/"
# 只带各语言 strings.json(与 install.sh 一致;settings_ui.json 仅走 pck,
# 松散放置会被游戏 mod 扫描器当 manifest 报错)
for f in src/ItsLoading/localization/*/strings.json; do
  lang_dir="${f%/strings.json}"; lang="${lang_dir##*/}"
  mkdir -p "$STAGE/localization/$lang"
  cp "$f" "$STAGE/localization/$lang/"
done

echo "==> 生成 VDF(${ITEM_ID:+更新物品 $ITEM_ID}${ITEM_ID:-首次上传,ID 待分配})"
cat > "$VDF" <<EOF
"workshopitem"
{
  "appid"              "$APP_ID"
  "publishedfileid"     "${ITEM_ID:-}"
  "contentfolder"       "$STAGE"
  "previewfile"         "$STAGE/mod_image.png"
  "visibility"          "0"
  "title"               "不再干等 · It's Loading"
  "description"         "启动进度条:工坊读取/模组加载/启动步骤全程可见,附带启动耗时瀑布图与主题切换。A boot progress bar + startup waterfall for Slay the Spire 2. Source: https://github.com/Somiona/sts2-its-loading"
  "changenote"          "v$VERSION:尝试修复windows崩溃问题"
}
EOF
cat "$VDF"

if [ "$STAGE_ONLY" = "--stage-only" ]; then
  echo "==> 仅暂存完成,未上传。内容清单:"; ls -R "$STAGE" | head -20
  exit 0
fi

echo "==> 上传(密码与 Steam Guard 在提示中输入)"
steamcmd +login "$ACCOUNT" +workshop_build_item "$VDF" +quit

echo
echo "完成。若是首次上传:把上面输出中的 Published File ID 填进 $ID_FILE,"
echo "并在浏览器里完善工坊页面的描述/标签(banner 图等)。"
