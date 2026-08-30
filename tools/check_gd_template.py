#!/usr/bin/env python3
"""gd 启动视图源文件静态门禁(build.sh 构建门禁)。

检查项:
  1. Godot 解析(找到可执行时;GODOT_BIN / PATH / /Applications):
     对每个 .gd 跑 --check-only 权威解析
  2. 契约完整性:boot.gd 必须保留桥协议方法/变量(C# GdBridgeBar 与 Handoff
     依赖);每个 themes/<id>/theme.gd 必须实现主题三动词(theme_build/apply/retire)
  3. 几何不变量(找到可执行时):tools/check_theme_geometry.gd headless 断言
     (滑段达轨满/滑段后 set_fraction 归位/全树填充不越轨)

用法:python3 tools/check_gd_template.py  (失败退出码 1,build.sh 的 set -e 生效)
"""
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GD_DIR = ROOT / "src" / "ItsLoading" / "Themes"

# boot.gd 的桥协议面(C# GdBridgeBar.TryBuild / BootSplash.Handoff 依赖)
BOOT_CONTRACT = [
    "csharp_attach", "csharp_present", "takeover",
    "get_workshop_log", "show_hint", "bridge_version", "boot_start_msec",
]
# 主题三动词(boot.gd 的 _apply/takeover 依赖)
THEME_CONTRACT = ["theme_build", "theme_apply", "theme_retire"]


def fail(msg: str) -> None:
    print(f"gd check: FAIL — {msg}", file=sys.stderr)
    sys.exit(1)


def find_godot() -> str | None:
    exe = os.environ.get("GODOT_BIN")
    if exe and Path(exe).exists():
        return exe
    found = shutil.which("godot") or shutil.which("godot4")
    if found:
        return found
    for app in sorted(Path("/Applications").glob("Godot*.app")):
        # 二进制名不保证与 .app 同名(Godot_mono.app 内是 Godot),两个候选都试
        for cand in (app / "Contents" / "MacOS" / app.stem,
                     app / "Contents" / "MacOS" / "Godot"):
            if cand.exists():
                return str(cand)
    return None


def check_parse(godot: str | None, path: Path) -> None:
    if not godot:
        return
    r = subprocess.run(
        [godot, "--headless", "--check-only", "-s", str(path)],
        capture_output=True, text=True, timeout=60)
    errors = [l for l in (r.stdout + r.stderr).splitlines()
              if "SCRIPT ERROR" in l or "Parse Error" in l]
    if errors:
        fail(f"{path.name} 解析失败:\n  " + "\n  ".join(errors[:5]))
    print(f"  解析({path.name}): 通过")


def check_geometry(godot: str | None) -> None:
    if not godot:
        print("  几何不变量: 跳过(未找到可执行;可设 GODOT_BIN 启用)")
        return
    script = ROOT / "tools" / "check_theme_geometry.gd"
    r = subprocess.run(
        [godot, "--headless", "-s", str(script)],
        capture_output=True, text=True, timeout=60, cwd=str(ROOT))
    out = r.stdout + r.stderr
    if r.returncode != 0 or "GEOM FAIL" in out:
        errs = [l for l in out.splitlines() if "FAIL" in l or "SCRIPT ERROR" in l]
        fail("几何不变量失败:\n  " + "\n  ".join(errs[:6]))
    print("  几何不变量(kit 裸控件 + 3 主题矩阵): 通过")


def check_contract(path: Path, symbols: list[str], what: str) -> None:
    src = path.read_text(encoding="utf-8")
    missing = [s for s in symbols if s not in src]
    if missing:
        fail(f"{path.name} 缺{what}: {missing}")


def main() -> None:
    files = sorted(GD_DIR.rglob("*.gd"))
    if not files:
        fail(f"{GD_DIR} 下没有 .gd 文件(目录结构变了?请同步本检查)")
    godot = find_godot()
    if not godot:
        print("  Godot 解析: 跳过(未找到可执行;可设 GODOT_BIN 启用)")

    print(f"gd source check({len(files)} 个文件):")
    check_contract(GD_DIR / "boot.gd", BOOT_CONTRACT, "桥协议面")
    for theme in sorted(GD_DIR.iterdir()):
        if theme.is_dir():
            check_contract(theme / "theme.gd", THEME_CONTRACT, "主题三动词")
    print("  契约(boot 桥协议 / 主题三动词): 通过")

    for f in files:
        check_parse(godot, f)
    check_geometry(godot)
    print("  OK")


if __name__ == "__main__":
    main()
