#!/bin/bash
# 构建 It's Loading(不再干等)
# 引用 dll 由 MSBuild 自动从本机游戏安装解析(三平台路径发现,见 src/ItsLoading/Sts2PathDiscovery.props)
set -euo pipefail
cd "$(dirname "$0")"

dotnet build src/ItsLoading/ItsLoading.csproj -c Release

echo
echo "dll => src/ItsLoading/bin/Release/net9.0/ItsLoading.dll"
