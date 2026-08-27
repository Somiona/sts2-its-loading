#!/usr/bin/env python3
"""i18n 一致性检查(build.sh 构建门禁)。

一张翻译表 = localization/<语言>/ 下两份文件:
  strings.json     运行时表(C# I18n 与 gd splash 共用,回退链 lang → eng → 键)
  settings_ui.json BaseLib 设置入口的游戏侧 loc(pck 打包,ITSLOADING- 前缀键)

检查项:
  1. 源码中实际使用的键(C# 的 I18n.T("...") 与 gd 模板的 _t("..."))
     必须存在于 eng/strings.json —— 缺 = 错误(构建失败)
  2. 其他语言相对 eng 的缺键 = 警告(逐键回退 eng,部分翻译可用)
  3. 有 strings.json 的语言必须有 settings_ui.json,且三个必需键齐全
     —— 缺 = 错误(否则 BaseLib 入口显示原始键名)

贡献一种新语言 = 复制 localization/eng/ 整个目录为 <语言代码>/,翻译两份 JSON;
本工具会在构建时指出还缺什么。
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "ItsLoading"
LOC = SRC / "localization"

REQUIRED_SETTINGS_KEYS = [
    "ITSLOADING-mod_title",
    "ITSLOADING-OPEN_WATERFALL.title",
    "ITSLOADING-VIEW.title",
]


def used_keys():
    """扫描源码中字面量使用的键(动态键如 I18n.T(s.Id) 不在其中,靠 eng 表人工保证)。"""
    keys = set()
    pat_cs = re.compile(r'I18n\.T\(\s*"([^"]+)"')
    pat_gd = re.compile(r'\b_txt\(\s*"([^"]+)"')
    for f in SRC.rglob("*.cs"):
        if "bin" in f.parts or "obj" in f.parts:
            continue
        text = f.read_text(encoding="utf-8")
        keys |= set(pat_cs.findall(text))
        keys |= set(pat_gd.findall(text))
    return keys


def load_table(path):
    if not path.exists():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except json.JSONDecodeError as e:
        print(f"ERROR {path}: JSON 解析失败 — {e}")
        return None


def main():
    errors, warnings = [], []

    eng = load_table(LOC / "eng" / "strings.json") or {}
    if not eng:
        errors.append("eng/strings.json 缺失或为空(基准表必须存在)")

    used = used_keys()
    missing = used - set(eng)
    if missing:
        errors.append(f"eng 缺少源码使用中的键: {sorted(missing)}")

    for d in sorted(LOC.iterdir()):
        if not d.is_dir():
            continue
        lang = d.name
        table = load_table(d / "strings.json")
        if table is None:
            continue
        if lang != "eng":
            miss = set(eng) - set(table)
            if miss:
                warnings.append(f"{lang} 缺 {len(miss)} 键(逐键回退 eng): {sorted(miss)}")
        ui = load_table(d / "settings_ui.json")
        if not ui:
            errors.append(f"{lang} 有 strings.json 但缺 settings_ui.json(BaseLib 入口会显示原始键名)")
        else:
            miss = [k for k in REQUIRED_SETTINGS_KEYS if k not in ui]
            if miss:
                errors.append(f"{lang}/settings_ui.json 缺键: {miss}")

    for w in warnings:
        print(f"WARN  {w}")
    for e in errors:
        print(f"ERROR {e}")
    total_langs = len([d for d in LOC.iterdir() if d.is_dir()])
    print(f"i18n check: {len(used)} used keys, eng {len(eng)} keys, "
          f"{total_langs} languages, {len(warnings)} warnings, {len(errors)} errors")
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
