# 不再干等 · It's Loading

> 修复了塔2开 mod 没有进度条的 bug。
>
> Fixes the bug where Slay the Spire 2 shows no loading progress with mods enabled.

<!-- TODO: 放一张启动过程的 GIF / 截图 -->

## 这是什么

装了一堆 mod 之后,每次启动游戏都要对着黑屏干等十几秒到半分钟,不知道是卡死了还是在加载,只能干瞪眼。

这个 mod 在游戏启动过程中会显示进度条,并且告诉你**现在到底在干什么**:

- 正在读取创意工坊订阅(第几个 / 共几个)
- 正在加载哪个模组、花了多少毫秒
- 游戏启动到哪一步(图集 / 本地化 / 模型数据库 / 资源预载……)

## 安装

**Steam 创意工坊**: 订阅即用。

**手动安装**:把 mod 文件夹放进游戏目录的 `mods/` 文件夹:

- Windows:`Slay the Spire 2/mods/`
- macOS:右键游戏 → 显示包内容 → `Contents/MacOS/mods/`

首次启动时会完成一次启动画面注入(左上角会有提示),**从下一次启动起**进度条全程可见。

## 卸载

在游戏内 mod 菜单关闭、退订、或直接删掉 mod 文件夹都可以。启动时检测到 mod 已关闭或移除,会自动清理注入的启动画面。

## 兼容性

- 在 Slay the Spire 2  v0.107.1上测试通过
- 和其他 mod 没有加载顺序冲突,不需要当前置

## 已知限制

- 创意工坊读取阶段的计数来自游戏日志,极少数情况下可能略有延迟

---

# It's Loading

> Fixes the bug where Slay the Spire 2 shows no loading progress with mods enabled.

## What is this

With a pile of mods installed, every game launch means staring at a black screen for ages, wondering whether it crashed or is just loading.

This mod shows a progress bar during game startup, telling you **what is actually happening**:

- Reading Steam Workshop subscriptions (which one / how many)
- Which mod is being loaded, down to the millisecond
- Which boot step the game is on (atlases / localization / model database / preload…)

## Install

**Steam Workshop**: subscribe and play.

**Manual**: drop the mod folder into the game's `mods/` directory:

- Windows: `Slay the Spire 2/mods/`
- macOS: right-click the app → Show Package Contents → `Contents/MacOS/mods/`

The first launch performs a one-time boot-splash injection (you'll see a notice). The progress bar covers the full startup **from the next launch on**.

## Uninstall

Turn it off in the in-game mod menu, unsubscribe, or delete the mod folder — whichever. On the next launch the mod detects it's gone and removes the injected boot splash automatically.

## Compatibility

- Tested on Slay the Spire 2 v0.107.1
- No load-order requirements, no conflicts with other mods

## Known Limitations

- Workshop reading counts come from the game log and may occasionally lag a little

---

*非官方社区 mod,与 MegaCrit 无关 · An unofficial community mod, not affiliated with MegaCrit.*
