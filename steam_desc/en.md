It's Loading — No More Staring at a Black Screen

Fixes the "bug" where Slay the Spire 2 shows no loading progress with mods enabled.
QQ group 1087829235

With a pile of mods installed, every game launch means staring at a black screen for ten seconds to half a minute, with no idea whether the game is frozen or still loading — nothing to do but watch.

This mod shows a progress bar during startup and tells you what is actually happening:

    Reading Steam Workshop subscriptions (which one / how many)
    Which mod is being loaded, down to the millisecond
    Which boot step the game is on (atlases / localization / model database / preload…)

[Themes]

Several themes are available — switch between them in the mod settings. Takes effect on the next launch.

[Waterfall chart]

Shows how long each mod took to load during startup, and exposes an API that other mods can read.

[Notes]

The progress bar for the pre-boot phase is not visible on the very first launch: the first start performs a one-time boot-screen injection (with a notice in the top-left corner). From the next launch on, the bar is visible for the entire startup.
Due to a Godot engine limitation, the bar may appear stuck in a few places, usually for about 3-5 seconds, then resumes scrolling normally.

> The public-beta branch is supported: the loading screen is drawn natively to work around the Godot engine's startup rendering limitation.
> (Toggle "(Beta) Native loading screen renderer" in the settings to disable.)

The file layout changed in this update, so the first launch after updating may show a glitched picture — restarting the game once restores everything to normal.


[Uninstall]

Disable it in the in-game mod menu, unsubscribe, or simply delete the mod folder — any of these works. On startup, once the mod is detected as disabled or removed, the injected boot screen cleans itself up automatically.

[Report issues]

Please file issues on the GitHub repo first: https://github.com/Somiona/sts2-its-loading — comments on the Workshop page may not be answered promptly. Feel free to join the QQ group as well: 1087829235.
