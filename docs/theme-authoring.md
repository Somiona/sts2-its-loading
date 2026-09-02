# 主题制作指南 / Theme Authoring Guide

一份 `theme.json` 声明整个加载画面。同一份文件驱动全部渲染器(gd 启动画面、
macOS 冻结期原生呈现面、游戏内画廊实时预览)—— 声明一次,处处一致。

渲染数据流与双路径约束见 [`render-architecture.md`](render-architecture.md)。

- 快速上手:复制仓库 `pack-template/` 改起
- 词汇表校验:`python3 tools/check_themes.py <主题目录>`(发布前必过)
- 实时预览:游戏内 设置 → 本 mod → **Theme gallery**(选主题,Apply 下次启动生效)
- 布局标定:设置里开 **(Debug) Developer calibration view** → 品红元素框 + 10% 网格

## 主题包 = 一个普通 mod

```
MyThemePack/                 ← 工坊发布这个文件夹
├── MyThemePack.json         ← 普通 mod manifest(id/name/version;无 dll 无 pck)
└── themes/
    └── my-theme/            ← 主题 id = 文件夹名(小写字母/数字/-/_)
        ├── theme.json
        └── *.png            ← 素材(路径相对本文件夹)
```

安装/卸载与普通 mod 完全一致(订阅即装、退订即走,无任何残留)。
id 冲突时内置主题赢;主题包之间按加载序先到先得。
主题是纯数据:词汇表封闭、无代码执行、素材路径不得逃逸出主题目录。

## theme.json 结构

```jsonc
{
  "format": 1,                                   // 词汇表版本(当前 1)
  "space": { "kind": "design", "w": 854, "h": 480 },  // 或 {"kind":"screen"}(视口像素)
  "elements": [ … ]                              // z 序 = 数组序
}
```

**空间**:`design` = 854×480(或自定义)设计画布等比居中缩放,推荐;
`screen` = 直接用视口像素(classic 底条用)。所有长度/字体 = 所选空间的单位。

## 元素词汇表

| 类型 | 字段 | 说明 |
|---|---|---|
| `bg` | `color` | 整屏底色(设计画布内) |
| `logo` | `src, x, y, w, fallback_text, fallback_font, fallback_color, nearest` | 高按图比例;缺图显示 fallback 文字 |
| `strip` | `h` | 底部条带容器(子元素经 `parent` 挂入,坐标相对条带) |
| `label` | `text, bind, x, y, w?, h?, font, color, align?, overrun?` | 文字;`align` 1=居中 2=右 |
| `version_label` | `prefix, x, y, w?, h?, font, color, align?` | 自动拼 mod 版本号 |
| `bar_solid` | `x, y, w, h, track, fill, bind, indeterminate?` | 实心条;`w:"fill"` = 满宽−2x |
| `bar_outline` | 同上 + `border_w, inset, border` | 描边条 |
| `icon_row` | `count, size, gap, cx, cy\|bottom, pivot?, src\|pattern+index_base, nearest?, placeholder?, enlarge?` | 等距图标行;`enlarge:{factor}` = 当前阶段放大(底边锚) |
| `dots` | `of, scale, color, cy` | 行间隙圆点(`of` = icon_row 的 id) |
| `mask_track` | `members, tint, bind:"local", indeterminate` | 剪贴蒙版分段填充;members = icon_row/dots id;域 = 首个 icon_row |
| `sprite` | `src, x, y, w, h, frame_w, frame_h, frames, fps, nearest, activity?` | 自主时钟精灵表动画；`activity:{frames_per_update}` 可随数据更新额外推进 |
| `log_column` | `x, y, lines, line_h, font, color, bind:"log"` | 竖列日志,最新在底,越旧越淡 |
| `log_rows` | `x, y, w, lines, per_line, sep, line_h, font, color, align?, overrun?, bind:"log"` | 整行淘汰日志;`align` 缺省为居中 |

**bind(数据绑定,封闭集)**:`overall` 全程进度 · `local` 阶段进度 ·
`stage` 当前阶段(icon_row enlarge)· `step` 阶段标题 · `detail` 细节行 ·
`log` 活动日志流。label 缺省 bind = `step`。

**text 文案**:`"text": "字面量"` 或 `"text": {"loc": "键"}`(经 mod 本地化表解析)。

**indeterminate(阶段无总量时的表现)**:
`{"mode":"pulse","min_w":60,"travel":160}` 宽度呼吸 ·
`{"mode":"slide","cycle_s":3.0}` 1/4 宽滑段扫过。

**颜色** 一律 `#RRGGBBAA`。

**跨渲染一致性**:`icon_row.pattern` 必须包含 `%d`;`cy` 与 `bottom`、`src` 与
`pattern` 都必须二选一。可选 `pivot` 仅支持 `center`/`bottom`。native adapter
只消费编译器展开后的资源、引用、默认值和几何；Godot adapter 的等价语义由同一
词汇表门禁与跨 renderer 回归约束。

**引用规则**:`parent`(strip)、`of`(dots)、`members`(mask_track)只能引用
**先出现**的元素 id。每个元素 id 唯一且必填。

**退场**由渲染器统一对主题根层执行；主题作者不需要、也不能在 `theme.json`
重复声明 fade。Godot 与 native 路径默认使用同一段约 0.4 秒的淡出生命周期。

## 优雅降级(逐元素)

未知类型 / 未知 bind / 引用缺失 → 只跳过该元素并记日志,主题其余部分照常。
整体 JSON 损坏 → 回退 classic。缺素材 → logo 显示 fallback 文字、icon 显示
占位色块、sprite 整元素跳过。

## 三份随附参考实现

`src/ItsLoading/themes/` 下:`classic`(screen 空间 + strip)、
`minespire`(design 空间 + logo/描边条/精灵/logo 兜底)、
`gachathespire`(icon_row + dots + mask_track + log_rows 全家福)。

## 已知平台差异(诚实声明)

冻结期(macOS 测试版分支)的原生呈现面中,文字用系统字体而非游戏字体,
字形略有差异;精灵动画按加载活动节拍步进。帧恢复后由游戏字体接管,两者
布局逐像素对齐(以标定视图验证)。
