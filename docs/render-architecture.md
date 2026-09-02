# Loading render architecture

## Invariants

- Runtime data only moves forward: `BootTimeline → LoadingPresentation → LoadingFrame → SurfaceRouter → renderer`.
- `LoadingFrame` is the only dynamic input accepted by either renderer.
- Godot is the baseline renderer. Native rendering is an optional freeze-time takeover, never a separate upstream pipeline.
- `SurfaceRouter` alone owns takeover, fallback and the default root-surface retirement lifecycle. Themes only stop their own animation; renderers never call each other or request timeline replay.
- `ThemeCompiler` owns C# theme defaults, references, asset expansion and geometry. `MacLayerSurface` only maps `ThemePlan` to CALayer objects.
- Theme discovery and gallery availability never depend on the native-renderer setting.
- Data presentation and visual motion are separate: snapshots change state; renderer-local clocks advance loops without producing upstream state.

## Runtime flow

```text
patch facts → BootTimeline → LoadingPresentation → LoadingFrame → SurfaceRouter
                                                                  ├─ Godot baseline
                                                                  └─ native takeover
```

The frame-0 Godot bootstrap is also the prelude input adapter. At handoff it transfers one immutable prelude snapshot to `LoadingPresentation` and stops collecting. The following replay is therefore the first complete C# frame; there is no ongoing two-way synchronization.

## Route policy

The native factory is supplied only when the user setting allows native rendering and the platform supports it. Without a factory the router is a cheap Godot-only path.

Godot remains mounted as standby. On a stable-frame freeze the router:

1. attaches native;
2. presents the latest frame to native;
3. hides Godot only after both operations succeed;
4. sends subsequent frames only to native.

If native later fails, the router tears it down, shows Godot and presents the saved latest frame. It does not rebuild Godot or call `BootTimeline.Replay()`.

The first `BootStage.Menu` frame is presented normally and then immediately starts the shared fade. Fade completion only leaves the surface transparent: native layers and image resources remain alive while Godot workers are still completing startup work. The later menu timer is the safe disposal point that detaches and releases native resources. Theme definitions choose neither timing.

## Motion policy

State-bound elements (labels, logs, determinate progress and stage markers) update only when a `LoadingFrame` arrives. Continuous elements own no application timer: Godot advances them from `_process`, while macOS installs compositor-side Core Animation loops that continue when the main thread is busy.

Sprites always run at their declared base `fps`. The optional `activity.frames_per_update` maps data cadence to motion by advancing extra animation time on each presentation. Frequent loading updates therefore make the sprite run faster; silence immediately returns it to the autonomous base rate without a watchdog, decay task or reverse data flow. Indeterminate bars and masks similarly start/stop a renderer-native loop on state transitions rather than sampling `LoadingFrame.T` as their frame clock.

## Theme flow

`theme.json` is the author-facing specification. The C# `ThemeCompiler` produces a deterministic `ThemePlan` with expanded icon sources, normalized defaults, resolved references and precomputed row/dot/mask geometry. Native adapters consume only this plan.

The compiler also records native incompatibilities. A theme requiring unsupported native semantics remains fully usable through Godot; the router simply receives no native factory. Unsupported behavior is never approximated silently.

The frame-0 Godot interpreter must read the author specification before C# exists. Cross-runtime equivalence is therefore enforced by the vocabulary validator plus mirrored adapter conformance checks. If a serialized active-plan cache is introduced later, it should replace this frame-0 interpretation rather than become a third semantic layer.

## Adding features

- Prefer lowering a new author-facing element to existing plan primitives.
- Add a new renderer primitive only when existing primitives cannot express the behavior.
- A new primitive requires validator coverage and conformance checks for every supported adapter.
- Unsupported native behavior must keep the Godot route active; adapters must not silently invent different semantics.
