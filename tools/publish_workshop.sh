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

echo "==> 生成 VDF($([ -n "$ITEM_ID" ] && echo "更新物品 $ITEM_ID" || echo "首次上传,ID 待分配"))"
# 描述:VDF 只上传 en 版(Steam 按语言分条存储,zhs/zht 在工坊页面的语言页签里维护)。
# changenote:CHANGELOG.md 的 Unreleased 区整体压缩(内部版本号不做分割),见该文件头部约定。
vdf_escape() { # 文件 → VDF 字符串值。换行保留为真实换行(steamcmd 的
  # workshop_build_item 不解释 \n 转义,2026-08-29 实测:转义会以字面 \n 显示在
  # 页面上);引号仍转义为 \",否则会截断字符串;内容不应含反斜杠。
  python3 - "$1" <<'PY'
import sys
t = open(sys.argv[1], encoding='utf-8').read().strip()
if '\\' in t:
    sys.exit('描述内容包含反斜杠,VDF 转义语义不确定——请先处理')
sys.stdout.write(t.replace('"', '\\"'))
PY
}
DESC_ESC=$(vdf_escape steam_desc/en.md)
CHANGENOTE_ESC=$(python3 - CHANGELOG.md "$VERSION" <<'PY'
import sys, re
text = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'^## Unreleased[ \t]*$\n?(.*?)(?=^## |\Z)',
              text, re.S | re.M)
body = (m.group(1) if m else '').strip()
if not body:
    sys.exit('CHANGELOG.md 的 Unreleased 区是空的——先写变化再发布')
note = f'v{sys.argv[2]}:\n' + '\n'.join(l.strip() for l in body.splitlines() if l.strip())
print(note.replace('"', '\\"'), end='')
PY
)
cat > "$VDF" <<EOF
"workshopitem"
{
  "appid"              "$APP_ID"
  "publishedfileid"     "${ITEM_ID:-}"
  "contentfolder"       "$STAGE"
  "previewfile"         "$STAGE/mod_image.png"
  "visibility"          "0"
  "title"               "It's Loading - Game Start Progress Bar"
  "description"         "$DESC_ESC"
  "changenote"          "$CHANGENOTE_ESC"
}
EOF
cat "$VDF"

if [ "$STAGE_ONLY" = "--stage-only" ]; then
  echo "==> 仅暂存完成,未上传。内容清单:"; ls -R "$STAGE" | head -20
  exit 0
fi

echo "==> 上传(密码与 Steam Guard 在提示中输入)"
steamcmd +login "$ACCOUNT" +workshop_build_item "$VDF" +quit

# 归档:Unreleased → 本次发布版本号(顶部重建空 Unreleased;内部版本号不单独成节)
python3 - CHANGELOG.md "$VERSION" <<'PY'
import sys, re, datetime
p, ver = sys.argv[1], sys.argv[2]
s = open(p, encoding='utf-8').read()
marked = f'## [v{ver}] — {datetime.date.today().isoformat()}'
s2, n = re.subn(r'^## Unreleased[ \t]*$', marked, s, count=1, flags=re.M)
if n == 0:
    print('!! CHANGELOG.md 未找到 Unreleased 区,请手动归档', file=sys.stderr)
    sys.exit(1)
m = re.search(r'^## ', s2, re.M)
s2 = s2[:m.start()] + '## Unreleased\n\n' + s2[m.start():]
open(p, 'w', encoding='utf-8').write(s2)
print(f'==> CHANGELOG.md: Unreleased 已归档为 [v{ver}]')
PY

echo
echo "完成。若是首次上传:把上面输出中的 Published File ID 填进 $ID_FILE,"
echo "并在浏览器里完善工坊页面的描述/标签(banner 图等)。"
echo "别忘了 zhs/zht 描述在工坊页面的语言页签里单独维护(steam_desc/ 下是源文件)。"
