#!/usr/bin/env python3
"""gd 启动脚本模板静态门禁(build.sh 构建门禁)。

BootSplash.cs 的 BootSplashGdTemplate 是 C# verbatim 字符串(@"..."):
GDScript 里的一个 `"` 必须写成 `""`。2026-08-29 实机事故:模板里空串只写了
两个引号,生成物变成裸引号 → 整个脚本 Parse Error → autoload 实例化失败,
gd 段从帧 0 起全灭(C# 测试与 @@token@@ 检查都查不出这一类损伤)。

检查项(不依赖 C# 常量真值,直接在模板文本上验证):
  1. 模板中每个 @@TOKEN@@ 在 BuildBootSplashGd() 里有对应 Replace 调用
  2. 把 @@TOKEN@@ 替换为 1.0、反转义 ""→" 之后:每行引号数必须为偶、
     括号必须配平(字符串与注释已剥离)
  3. 若找到 Godot 可执行(GODOT_BIN / PATH / /Applications),对替换后的
     脚本跑一次 --check-only 权威解析;找不到则跳过(仅警告性提示)

用法:python3 tools/check_gd_template.py  (失败退出码 1,build.sh 的 set -e 生效)
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BOOT_SPLASH = ROOT / "src" / "ItsLoading" / "BootSplash.cs"


def fail(msg: str) -> None:
    print(f"gd template check: FAIL — {msg}", file=sys.stderr)
    sys.exit(1)


def extract_template(src: str) -> str:
    m = re.search(
        r'private const string BootSplashGdTemplate = @"extends Node(.*?)\n";\n',
        src, re.S)
    if not m:
        fail("BootSplashGdTemplate 未找到(模板结构变了?请同步本检查)")
    return "extends Node" + m.group(1) + "\n"


def check_tokens(src: str, template: str) -> None:
    used = set(re.findall(r"@@[A-Z_]+@@", template))
    provided = set(re.findall(r'\.Replace\("(@@[A-Z_]+@@)"', src))
    missing = used - provided
    if missing:
        fail(f"模板 token 无 Replace 提供: {sorted(missing)}")
    print(f"  tokens: {len(used)} 个全部有 Replace")


def materialize(template: str) -> str:
    # 哑替换需按类型:颜色 token 落在 Color 形参位(1.0 会触发类型错误),
    # 其余 token 数值位置合法;字符串位置(如 "".."" 内)任何字面量都无害
    out = re.sub(r"@@[A-Z_]*COLOR@@", "Color(1, 1, 1, 1)", template)
    out = re.sub(r"@@[A-Z_]+@@", "1.0", out)
    return out.replace('""', '"')


def check_quotes_and_parens(script: str) -> None:
    bad = [f"{i}: {line}" for i, line in enumerate(script.splitlines(), 1)
           if line.count('"') % 2]
    if bad:
        fail("奇数引号(verbatim 转义断裂,会生成裸引号):\n  " + "\n  ".join(bad))
    depth = 0
    for i, line in enumerate(script.splitlines(), 1):
        code = re.sub(r'"[^"]*"', '""', line)   # 剥字符串字面量
        code = re.sub(r"#.*$", "", code)         # 剥注释
        depth += code.count("(") - code.count(")")
        if depth < 0:
            fail(f"第 {i} 行括号提前闭合")
    if depth != 0:
        fail(f"括号不配平(净深度 {depth})")
    print("  引号奇偶 / 括号配平: 通过")


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


def check_parse(script: str) -> None:
    godot = find_godot()
    if not godot:
        print("  Godot 解析: 跳过(未找到可执行;可设 GODOT_BIN 启用)")
        return
    with tempfile.NamedTemporaryFile("w", suffix=".gd", delete=False) as f:
        f.write(script)
        path = f.name
    try:
        r = subprocess.run(
            [godot, "--headless", "--check-only", "-s", path],
            capture_output=True, text=True, timeout=60)
    finally:
        os.unlink(path)
    errors = [l for l in (r.stdout + r.stderr).splitlines()
              if "SCRIPT ERROR" in l or "Parse Error" in l]
    if errors:
        fail("Godot 解析失败:\n  " + "\n  ".join(errors[:5]))
    print(f"  Godot 解析({Path(godot).name}): 通过")


def main() -> None:
    src = BOOT_SPLASH.read_text(encoding="utf-8")
    template = extract_template(src)
    print("gd template check:")
    check_tokens(src, template)
    script = materialize(template)
    check_quotes_and_parens(script)
    check_parse(script)
    print("  OK")


if __name__ == "__main__":
    main()
