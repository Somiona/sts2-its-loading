#!/bin/bash
# 构建 + 安装到 CrossOver/Wine 瓶内的 Windows 版 STS2。
# 路径来自 .env 的 STS2_WIN_GAME_DIR(模板见 .env.example);含全部门禁与测试。
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .env ] && source .env
GAME_DIR="${STS2_WIN_GAME_DIR:?请在 .env 设置 STS2_WIN_GAME_DIR(参考 .env.example)}"

echo "==> 门禁 + mac 构建(pck 平台无关,复用其产物)"
./build.sh

echo "==> Windows 引用构建(瓶内程序集:$GAME_DIR)"
dotnet build src/ItsLoading/ItsLoading.csproj -c Release --nologo \
    -p:Sts2Dir="$GAME_DIR" -p:Sts2DataDir="$GAME_DIR/data_sts2_windows_x86_64"
dotnet build src/ItsLoadingCompat/ItsLoadingCompat.csproj -c Release --nologo \
    -p:Sts2Dir="$GAME_DIR" -p:Sts2DataDir="$GAME_DIR/data_sts2_windows_x86_64"

GAME_DIR="$GAME_DIR" ./install.sh
