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

dotnet build src/ItsLoading/ItsLoading.csproj -c Release
dotnet build src/ItsLoadingCompat/ItsLoadingCompat.csproj -c Release

echo
echo "dll => src/ItsLoading/bin/Release/net9.0/ItsLoading.dll"
