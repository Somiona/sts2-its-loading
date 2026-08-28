It's Loading — No More Staring at a Black Screen

Fixes the bug where Slay the Spire 2 shows no loading progress with mods enabled.

With a pile of mods installed, every game launch means staring at a black screen for ten seconds to half a minute, with no idea whether the game is frozen or still loading.

This mod shows a progress bar during startup and tells you what is actually happening:

    Reading Steam Workshop subscriptions (which one / how many)
    Which mod is being loaded, down to the millisecond
    Which boot step the game is on (atlases / localization / model database / preload…)

[Notes]

The progress bar for the pre-boot phase is not visible on the very first launch: the first start performs a one-time boot-screen injection (with a notice in the top-left corner). From the next launch on, the bar is visible for the entire startup.
Near the end of stage 1/7 the bar may appear stuck for about 3-5 seconds due to a Godot engine limitation; it resumes scrolling normally afterwards.

[Uninstall]

Disable it in the in-game mod menu, unsubscribe, or simply delete the mod folder — any of these works. On startup, once the mod is detected as disabled or removed, the injected boot screen cleans itself up automatically.

[Report issues]

Please file issues on the GitHub repo first: https://github.com/Somiona/sts2-its-loading — comments on the Workshop page may not be answered promptly. Chinese-speaking community: QQ group 1087829235.
