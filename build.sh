#!/bin/bash
# 构建 It's Loading(不再干等)
# 引用 dll 由 MSBuild 自动从本机游戏安装解析(三平台路径发现,见 src/ItsLoading/Sts2PathDiscovery.props)
set -euo pipefail
cd "$(dirname "$0")"

# BaseLib(软依赖)引用:从本机工坊订阅复制到 refs/(不入库)
if [[ ! -f src/ItsLoading/refs/BaseLib.dll ]]; then
  BASELIB_DLL=$(find "$HOME/Library/Application Support/Steam/steamapps/workshop" \
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

dotnet build src/ItsLoading/ItsLoading.csproj -c Release
dotnet build src/ItsLoadingCompat/ItsLoadingCompat.csproj -c Release

# 本地化 pck(v3 格式,游戏的 4.5.1 fork 实测可加载;勿用 Godot 4.7 PCKPacker——它写 v4 被拒)
# 语言列表来自 localization/ 目录,新增语言无需改这里
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
