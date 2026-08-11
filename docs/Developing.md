# Developing

## Building

1. Clone the repo next to a working Dalamud plugin dev setup
2. Ensure `DalamudLibPath` points to your Dalamud `dev` folder (default: `%AppData%\XIVLauncher\addon\Hooks\dev\`)
3. `dotnet build`
4. Copy the output DLL to your Dalamud dev plugin folder

Dalamud loads the plugin from `bin/Debug/…` for local development — that path is recorded in
`dalamudConfig.json`. Release exists only to produce `latest.zip`; building it does not change what
you are running in game, and vice versa.

## UI scaling

Two rules, both learned the hard way.

**Every fixed pixel size goes through `UiScale.S(…)`.** The factor is `ImGuiHelpers.GlobalScale`,
which is Dalamud's **Global Font Scale** setting and nothing else — deliberately not derived from
the bound font's size, which folds in Windows DPI the user never asked for. Card and panel widths,
window sizes and minimums, column geometry and hard-coded button widths all read through it. A raw
literal is a bug: at 300% the text grows and the box holding it does not, so the label clips.

**The exception is `Window.Size` and `Window.SizeConstraints`.** Dalamud scales those itself —
`IWindow`'s documentation says so outright — so they stay in unscaled units. Running them through
`UiScale` scales them twice.

That also means `ImGui.GetWindowSize()`, which reports real pixels, must be divided by
`UiScale.Factor` before being remembered and handed back as `Size`. Storing pixels and returning them
as an already-scaled property is a feedback loop that multiplies the window by the scale on every
frame it is applied; it grew the main window to tens of thousands of pixels wide, past the point of
being draggable back. Raw ImGui calls — `SetNextWindowSize`, `BeginChild`, `SetNextItemWidth`, button
sizes — are not scaled by anyone and do need `UiScale`.

Window `Size` and `SizeConstraints` are set in `PreDraw`, not in the constructor, so they follow the
setting being changed while the plugin is running.

A window also has to be **resized** when the scale changes, not merely re-constrained. A scaled
`MinimumSize` forces the window larger when the setting goes up, but a smaller minimum only
*permits* a smaller window — it will not pull one back down, so it stays stranded at its enlarged
size until dragged by hand. Both windows compare `UiScale.Factor` against the last frame's and
re-apply `Size` with `ImGuiCond.Always` when it moves. No arithmetic on the size is needed: it is
held unscaled, and Dalamud multiplies it up.

**Every window pushes `FontScope` in `PreDraw` and pops it in `PostDraw`.** Without it a plugin
window renders at a third of the intended font size while Dalamud's own windows render correctly.
It also goes wrong *inconsistently*: ImGui passes a window's font scale down only one level of child
nesting ([ocornut/imgui#2701](https://github.com/ocornut/imgui/issues/2701)), so content one child
deep stays small while content two deep springs back to full size — which looks like two different
fonts in one window.

`PreDraw`/`PostDraw` rather than `Draw`, because those bracket ImGui's `Begin`, and the title bar is
drawn by `Begin`. Pushing inside `Draw` fixes the body and leaves the title bar small.

If you add a new `Window`, it needs both.

## IPC surface used

`Penumbra.Api.xml` ships next to `Penumbra.dll` in the installed plugin folder and is the
authoritative reference for these labels and signatures — check it before assuming call semantics.

### Penumbra (V5)

| Call | Purpose |
|---|---|
| `Penumbra.GetCollections.V5` | List available collections |
| `Penumbra.GetModList` | List all installed mods |
| `Penumbra.GetCurrentModSettings.V5` | Read a mod's enabled state and option selections |
| `Penumbra.TrySetMod.V5` | Enable or disable a mod in a collection |
| `Penumbra.TrySetModSetting.V5` | Set a **Single**-select option group (one option) |
| `Penumbra.TrySetModSettings.V5` | Set a **Multi**-select option group (full list at once) |
| `Penumbra.GetModDirectory` | Get the Penumbra mods root folder path |
| `Penumbra.RedrawObject.V5` | Force a redraw of the local player |
| `Penumbra.GameObjectRedrawn.V3` | Event fired after a redraw completes |

> **Single vs Multi is not interchangeable.** `TrySetModSetting.V5` sets a group *to* the option you
> pass — on a Multi group that **replaces** the whole selection rather than adding to it. Looping it
> over a checkbox group leaves only one box ticked, and if you cache the group's prior state before
> the loop it will oscillate between options on successive applies while still returning `Success`
> every time. Always use the plural `TrySetModSettings.V5` for Multi groups.

### Glamourer

| Call | Purpose |
|---|---|
| `Glamourer.SetItem.V3` | Equip a specific FFXIV item in one slot on the local player |
| `Glamourer.GetState` | Read the full state object; used to detect what is equipped per slot |
| `Glamourer.SetMetaState` | Toggle metadata state (visor, hat visibility, etc.) |
| `Glamourer.GetStateBase64` / `ApplyState` / `RevertState` | Legacy outfit system, retained for compatibility |

`applyFlags = 0` on `SetItem.V3` is the persistent mode that goes through Glamourer's state machine
rather than a one-shot apply.

All IPC calls are wrapped in try/catch; failures are logged as warnings rather than crashing.

## Mod format

Penumbra mods come in two layouts. `FileVersion` 3 keeps option groups in separate
`group_*.json` files; `FileVersion` 4 puts them in `meta.json`. Both are supported.

Slot detection matches material and texture paths as well as models — a mod that ships only a
material replacement for a slot still registers.

## Archived work

**Wardrobe sharing** — a WebSocket-based feature letting another player view your wardrobe and
send wear/unequip commands, gated by a viewer allowlist. It was scaffolded but never had a backend,
so it was removed before the public release rather than ship dead settings UI.

The removed pieces were `WardrobeShareService`, `RemoteCommand`, `WardrobeSnapshot`, the
`ShareServerUrl` / `ShareAllowlist` config fields and `PluginUi.DrawShareSettings`. They are
recoverable from the history before `3950276`. `WardrobeService.WardrobeChanged` is still raised and
was the hook the service subscribed to.

Anything built here would have to meet Dalamud's
[backend server rules](https://dalamud.dev/plugin-development/technical-considerations/): minimal
data, client-side hashing of player information, opt-in telemetry, HTTPS via a DNS hostname, and no
way to test whether a given player uses the plugin. The removed allowlist keyed on plain character
names would not have passed that last one.
