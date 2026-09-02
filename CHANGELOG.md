# 更新日志 / Changelog

约定: 每个功能/修复验收成功后立即在本文件追加条目(内部版本号照常按开发逻辑滚动,
不在本文件单独成节);`tools/publish_workshop.sh` 上传时把 Unreleased 区整体压成一条
changenote 随版本号发出,上传成功后自动把该区改标为发布的版本号与日期,
并在顶部重建空的 Unreleased 区。条目格式:中文一行 + 缩进的英文翻译行。

## Unreleased

- 任何人都能做主题啦:一份 theme.json 纯数据就是一个主题包,以普通 mod 的形式发布;启动画面、冻结期原生渲染与画廊预览共用同一份声明,零代码零编译喵
  - Anyone can make a theme now: one pure-data theme.json shipped as an ordinary mod ("theme pack") drives the boot splash, the freeze-phase native renderer, and the gallery preview from the same declaration — no code, no compiling nya
- 设置里新增主题画廊:列出全部已装主题并实时预览,点 Apply 下次启动生效,原来的下拉框退役喵
  - A theme gallery has arrived in the settings: it lists every installed theme with a live animated preview; Apply takes effect from the next launch — the old dropdown is retired nya
- 测试版(beta)分支的冻结期现在由原生渲染直接画完整主题画面:进度条、日志、精灵全部实时更新,直到主菜单淡出喵(设置里「(Beta) Native loading screen renderer」可关)
  - On the public-beta branch, the frozen phase is now drawn natively with the full themed screen — bars, logs and sprites all update live, fading out at the main menu nya (toggle "(Beta) Native loading screen renderer" in the settings to turn it off)
- 主题视觉收敛为单一来源:文案包装、活动日志(含工坊扫描前奏)、不定进度时钟在两个渲染器间逐字节一致;新增开发者标定视图供布局比对喵
  - Theme visuals now have a single source of truth: text wrapping, the activity log (including the workshop-scan prelude) and the indeterminate clock are byte-identical across both renderers; a developer calibration view is available for layout comparison nya

## [v0.19.0] — 2026-09-01

- 测试版(beta)分支现在也能完整显示启动画面的活动日志了喵～
  - The beta branch now shows the boot screen's activity log in full as well nya
- 瀑布图改成了分层视图,模组和内部步骤想看多细就展开多细,还有一键全部展开喵
  - The waterfall chart is now a layered view — expand mods and their internal steps as deep as you like, plus a one-click expand-all nya
- 瀑布图补上了以前看不见的几段空白,云同步和开场动画的耗时也画出来了喵
  - Several previously invisible gaps in the waterfall are now drawn, including cloud sync and the intro animation nya
- 启动画面的步骤刻度更细了,现在连读取存档的过程都能看到喵
  - The boot screen now ticks through finer steps — you can even watch it read your saves nya

## [v0.18.0] — 2026-08-31

- 主题的底层整个换了个更聪明的写法,以后出新主题会快很多,三款现有主题长相不变喵
  - The theme internals were rebuilt on a much smarter foundation — future themes will arrive faster, and the three existing ones look exactly the same nya
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
