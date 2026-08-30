# 更新日志 / Changelog

约定: 每个功能/修复验收成功后立即在本文件追加条目(内部版本号照常按开发逻辑滚动,
不在本文件单独成节);`tools/publish_workshop.sh` 上传时把 Unreleased 区整体压成一条
changenote 随版本号发出,上传成功后自动把该区改标为发布的版本号与日期,
并在顶部重建空的 Unreleased 区。条目格式:中文一行 + 缩进的英文翻译行。

## Unreleased

- 新主题来啦!经典抽卡游戏风格的主题,在设置里选择,下次启动生效喵
  - A new theme has arrived~ a classic gacha-game-style theme. Pick it in the settings, active from the next launch nya

## [v0.16.1] — 2026-08-30

- 新主题来啦! Minespire: Minecraft 风格的整屏红底居中双条。在设置里选择,下次启动生效喵
  - A new theme has arrived~ Minespire: a Minecraft-style full-screen red with centered dual bars, the activity log moved to the bottom-left, and a little running fox in the bottom-right corner, gently fading out when boot completes. Pick it in the settings, active from the next launch nya
- 主题切换从循环按钮换成了下拉框,一眼选中想要的那款,不用再连着点了喵
  - Theme switching is now a dropdown instead of a cycle button — pick the one you want at a glance, no more tapping through nya
- 小狐狸是 NeoForge 项目的开源素材,谢谢他们的贡献喵
  - The fox comes from the NeoForge project's open-source assets, thanks for sharing nya

## [v0.15.13] — 2026-08-29

- 进度条升级成双条啦～上面看整体进度,下面看当前阶段,一眼就知道加载到哪了喵
  - The progress bar is now a dual bar~ overall progress on top, current stage below, so you can tell where the boot is at a glance nya
- 从开机到主菜单全程同一条进度条陪伴,再也没有烦人的闪烁了喵(首次安装后的第一次启动会先注入,第二次起全程可见)
  - One single bar accompanies you from launch all the way to the main menu, no more annoying flickering nya (the first launch after installing performs the injection; fully visible from the second launch on)
- 左下角新增活动小日志,正在读哪个工坊项、加载哪个模组、花了多久全都报给你喵
  - A new activity log in the bottom-left reports which Workshop item is being read, which mod is loading, and how long each one takes nya
- 加载慢的大模组终于能看穿真相了,是初始化慢还是资源包慢一目了然喵～
  - Slow-loading mods can no longer hide~ see at a glance whether it's the initializer or the resource pack dragging nya
- 瀑布图大翻新,刻度对齐修好了、支持横向滚动、名称列牢牢钉住,还多了折叠和详细两种模式喵
  - The waterfall chart got a big makeover~ ruler alignment fixed, horizontal scrolling, a pinned name column, plus new collapsed and detailed modes nya
- 瀑布图横向滚到头也不怕看漏啦,最后一个条后面会留一小段空白,收尾看得清清楚楚喵～
  - Scroll to the very end without missing anything~ a bit of blank space follows the last bar so the tail stays fully visible nya
- 鼠标指到左边的名字或右边的条,两边会一起亮起来,对应关系一眼就找到了喵
  - Hover a name on the left or a bar on the right and both light up together, easy to match nya
- 工坊扫描每个订阅项的耗时也画进瀑布图了,谁在拖后腿当场抓获喵～
  - Per-item Workshop scan times are now drawn in the waterfall~ whoever slows things down gets caught on the spot nya
- 开放了启动耗时查询接口,别的 mod 也能来蹭数据了喵
  - A boot-timing query API is now open, other mods can come borrow the data nya
- 修好了启动画面可能加载失败的问题,现在稳稳的喵
  - Fixed an issue where the boot screen could fail to load, running rock solid now nya

## [v0.14.1] — 2026-08-28

- 修了 Windows 下打开瀑布图可能崩溃的问题,现在可以放心点开喵
  - Fixed a crash when opening the waterfall chart on Windows, safe to open now nya
