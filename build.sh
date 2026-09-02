#!/bin/bash
# 构建 It's Loading
# 引用 dll 由 MSBuild 自动从本机游戏安装解析(三平台路径发现,见 src/ItsLoading/Sts2PathDiscovery.props)
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] && source .env
STS2_STEAMAPPS="${STS2_STEAMAPPS:-$HOME/Library/Application Support/Steam/steamapps}"

# BaseLib(软依赖)引用:从本机工坊订阅复制到 refs/(不入库)
if [[ ! -f src/ItsLoading/refs/BaseLib.dll ]]; then
  BASELIB_DLL=$(find "$STS2_STEAMAPPS/workshop" \
      -path "*BaseLib/BaseLib.dll" 2>/dev/null | head -1)
  if [[ -z "$BASELIB_DLL" ]]; then
    echo "!! 未找到 BaseLib.dll(需要订阅 BaseLib 用于编译,运行时仍是软依赖)" >&2
    exit 1
  fi
  mkdir -p src/ItsLoading/refs
  cp "$BASELIB_DLL" src/ItsLoading/refs/BaseLib.dll
  echo "BaseLib ref: $BASELIB_DLL"
fi

# i18n 门禁:eng 缺使用中的键 = 构建失败(set -e 生效);其他语言缺键出警告
python3 tools/check_i18n.py

# gd 源文件门禁:解析检查 + 桥协议/主题契约完整性 + 几何不变量
# (C# 测试覆盖不到 gd 文本)
python3 tools/check_gd_template.py

# theme.json 词汇表门禁(声明式主题的闭环校验;亦可独立给主题作者用)
python3 tools/check_themes.py

# 时间线数学回归(纯 BCL,离线跑;本机仅 .NET 10 运行时,故测试工程 target net10.0)
dotnet test tests/ItsLoadingTimeline.Tests/ItsLoadingTimeline.Tests.csproj -c Release

dotnet build src/ItsLoading/ItsLoading.csproj -c Release
dotnet build src/ItsLoadingCompat/ItsLoadingCompat.csproj -c Release

# 本地化 pck(v3 格式,游戏的 4.5.1 fork 可加载;勿用 Godot 4.7 PCKPacker——
# 它写 v4,会被游戏拒绝)。语言列表来自 localization/ 目录,新增语言无需改这里
OUT="src/ItsLoading/bin/Release/net9.0"
PCK_ARGS=()
for ui in src/ItsLoading/localization/*/settings_ui.json; do
  lang_dir="${ui%/settings_ui.json}"
  lang="${lang_dir##*/}"
  PCK_ARGS+=("ItsLoading/localization/$lang/settings_ui.json" "$ui")
done
python3 tools/build_pck.py "$OUT/ItsLoading.pck" "${PCK_ARGS[@]}"

echo
echo "dll => src/ItsLoading/bin/Release/net9.0/ItsLoading.dll"
