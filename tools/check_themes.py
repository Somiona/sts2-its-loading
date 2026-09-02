#!/usr/bin/env python3
"""theme.json 声明式主题的构建门禁(build.sh 调用;亦可独立给主题作者用)。

词汇表 v1(format:1)与 render/interpreter.gd 的实现闭环:
  未知元素类型 / 未知 bind / 非法颜色 / 非数字几何 / 重复 id /
  parent 引用未先出现 / indeterminate 参数缺失 / format 版本不符 = 构建失败。
每个主题目录必须至少有 theme.json 或 theme.gd 之一(过渡期两者可并存)。

用法:python3 tools/check_themes.py   (失败退出码 1,set -e 生效)
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
THEMES_DIR = ROOT / "src" / "ItsLoading" / "themes"

FORMAT_VERSION = 1
KNOWN_TYPES = {"bg", "logo", "strip", "label", "version_label", "bar_solid",
               "bar_outline", "icon_row", "dots", "mask_track", "sprite",
               "log_column", "log_rows"}
KNOWN_BINDS = {"overall", "local", "step", "detail", "log", "stage"}
CONTAINER_TYPES = {"strip"}
ROW_TYPES = {"icon_row"}          # mask_track 成员 / dots.of 可引用
COLOR_RE = re.compile(r"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")

# 各类型的必填数值/颜色字段(数值校验 float 可解析;颜色校验 #RRGGBBAA)
NUMERIC_FIELDS = {
    "strip": ["h"],
    "label": ["x", "y", "font"],
    "version_label": ["x", "y", "font"],
    "bar_solid": ["x", "y", "h"],
    "bar_outline": ["x", "y", "h", "border_w", "inset"],
    "logo": ["x", "y", "w", "fallback_font"],
    "icon_row": ["count", "size", "gap", "cx"],
    "dots": ["scale", "cy"],
    "sprite": ["x", "y", "w", "h", "frame_w", "frame_h", "frames", "fps"],
    "log_column": ["x", "y", "lines", "line_h", "font"],
    "log_rows": ["x", "y", "w", "lines", "per_line", "line_h", "font"],
}
COLOR_FIELDS = {
    "bg": ["color"],
    "label": ["color"],
    "version_label": ["color"],
    "bar_solid": ["track", "fill"],
    "bar_outline": ["border", "fill"],
    "logo": ["fallback_color"],
    "dots": ["color"],
    "mask_track": ["tint"],
    "log_column": ["color"],
    "log_rows": ["color"],
}


def fail(msg: str) -> None:
    print(f"themes check: FAIL — {msg}", file=sys.stderr)
    sys.exit(1)


def check_number(theme: str, eid: str, field: str, v) -> None:
    if not isinstance(v, (int, float)) or isinstance(v, bool):
        fail(f"{theme}/{eid}: 字段 {field}={v!r} 不是数字")


def check_color(theme: str, eid: str, field: str, v) -> None:
    if not isinstance(v, str) or not COLOR_RE.match(v):
        fail(f"{theme}/{eid}: 颜色 {field}={v!r} 不匹配 #RRGGBBAA")


def check_theme_json(path: Path) -> None:
    theme = path.parent.name
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        fail(f"{theme}/theme.json: JSON 解析失败 — {e}")
    if not isinstance(data, dict):
        fail(f"{theme}/theme.json: 顶层不是对象")

    if data.get("format") != FORMAT_VERSION:
        fail(f"{theme}: format={data.get('format')!r} ≠ {FORMAT_VERSION}")

    space = data.get("space", {"kind": "screen"})
    if not isinstance(space, dict) or space.get("kind") not in ("screen", "design"):
        fail(f"{theme}: space 非法:{space!r}(应为 screen 或 design)")
    if space.get("kind") == "design":
        for k in ("w", "h"):
            check_number(theme, "(space)", k, space.get(k, 854 if k == "w" else 480))

    elements = data.get("elements")
    if not isinstance(elements, list) or not elements:
        fail(f"{theme}: elements 缺失或为空")

    seen_ids: set[str] = set()
    seen_rows: set[str] = set()   # icon_row(dots.of / mask.members 可引用)
    seen_dots: set[str] = set()   # dots(mask.members 可引用)
    for i, e in enumerate(elements):
        if not isinstance(e, dict):
            fail(f"{theme}: elements[{i}] 不是对象")
        etype = e.get("type")
        if etype not in KNOWN_TYPES:
            fail(f"{theme}: elements[{i}] 未知类型 {etype!r}(可用:{sorted(KNOWN_TYPES)})")
        eid = e.get("id")
        if not isinstance(eid, str) or not eid:
            fail(f"{theme}: elements[{i}]({etype})缺 id")
        if eid in seen_ids:
            fail(f"{theme}: 重复 id {eid!r}")
        seen_ids.add(eid)
        if etype in ROW_TYPES:
            seen_rows.add(eid)
        if etype == "dots":
            seen_dots.add(eid)

        parent = e.get("parent", "")
        if parent != "":
            if parent not in seen_ids or parent == eid:
                fail(f"{theme}/{eid}: parent {parent!r} 未先出现(容器须在子元素之前)")

        for f in NUMERIC_FIELDS.get(etype, []):
            if f in e:
                check_number(theme, eid, f, e[f])
        for f in COLOR_FIELDS.get(etype, []):
            if f in e:
                check_color(theme, eid, f, e[f])
        if "w" in e and e["w"] != "fill":
            check_number(theme, eid, "w", e["w"])

        bind = e.get("bind", "")
        if bind != "":
            if bind not in KNOWN_BINDS:
                fail(f"{theme}/{eid}: 未知 bind {bind!r}(可用:{sorted(KNOWN_BINDS)})")
            if etype in ("bar_solid", "bar_outline") and bind not in ("overall", "local"):
                fail(f"{theme}/{eid}: bar 只能绑 overall/local,得到 {bind!r}")
            if etype in ("log_column", "log_rows") and bind != "log":
                fail(f"{theme}/{eid}: 日志只能绑 log")

        # 类型专属结构校验
        if etype == "icon_row":
            if "cy" not in e and "bottom" not in e:
                fail(f"{theme}/{eid}: icon_row 需要 cy 或 bottom 之一")
            if "src" not in e and "pattern" not in e:
                fail(f"{theme}/{eid}: icon_row 需要 src 或 pattern")
            if "pattern" in e and "index_base" not in e:
                fail(f"{theme}/{eid}: pattern 需要 index_base")
            if "enlarge" in e:
                en = e["enlarge"]
                if not isinstance(en, dict) or "factor" not in en:
                    fail(f"{theme}/{eid}: enlarge 需要 {{factor}}")
                else:
                    check_number(theme, eid, "enlarge.factor", en["factor"])
        if etype == "dots":
            of = e.get("of")
            if not isinstance(of, str) or of not in seen_rows:
                fail(f"{theme}/{eid}: dots.of={of!r} 未先出现(须是先前的 icon_row id)")
        if etype == "mask_track":
            members = e.get("members")
            if not isinstance(members, list) or not members:
                fail(f"{theme}/{eid}: mask_track.members 缺失或为空")
            else:
                for m in members:
                    if not isinstance(m, str) or m not in (seen_rows | seen_dots):
                        fail(f"{theme}/{eid}: mask 成员 {m!r} 未先出现(须是先前的 icon_row/dots id)")
            if e.get("bind", "") != "local":
                fail(f"{theme}/{eid}: mask_track 只能绑 local")
            if "indeterminate" not in e:
                fail(f"{theme}/{eid}: mask_track 需要 indeterminate")
        if etype == "version_label" and not isinstance(e.get("prefix"), str):
            fail(f"{theme}/{eid}: version_label 需要 prefix 字符串")
        if etype in ("logo", "sprite") and not isinstance(e.get("src"), str):
            fail(f"{theme}/{eid}: {etype} 需要 src 字符串")
        for f in ("src", "pattern"):
            v = e.get(f)
            if isinstance(v, str) and (v.startswith("/") or ".." in v
                    or v.startswith("res://") or v.startswith("user://")):
                fail(f"{theme}/{eid}: {f} 路径逃逸 '{v}'(须为主题目录内相对路径)")

        ind = e.get("indeterminate")
        if ind is not None:
            if not isinstance(ind, dict):
                fail(f"{theme}/{eid}: indeterminate 不是对象")
            mode = ind.get("mode")
            if mode == "pulse":
                for f in ("min_w", "travel"):
                    if f not in ind:
                        fail(f"{theme}/{eid}: pulse 缺 {f}")
            elif mode == "slide":
                if "cycle_s" not in ind:
                    fail(f"{theme}/{eid}: slide 缺 cycle_s")
            else:
                fail(f"{theme}/{eid}: indeterminate.mode={mode!r} 非法(pulse|slide)")

        text = e.get("text")
        if text is not None and not isinstance(text, (str, dict)):
            fail(f"{theme}/{eid}: text 只能是字符串或 {{\"loc\": 键}}")
        if isinstance(text, dict) and not isinstance(text.get("loc"), str):
            fail(f"{theme}/{eid}: text.loc 必须是字符串")


def main() -> None:
    if not THEMES_DIR.is_dir():
        fail(f"主题目录不存在:{THEMES_DIR}")
    json_count = 0
    for d in sorted(THEMES_DIR.iterdir()):
        if not d.is_dir():
            continue
        if not (d / "theme.json").exists():
            fail(f"{d.name} 缺 theme.json(theme.gd 已退役)")
        json_count += 1
        check_theme_json(d / "theme.json")
    print(f"themes check: {json_count} 个 theme.json 通过词汇表 v{FORMAT_VERSION} 校验")


if __name__ == "__main__":
    main()
