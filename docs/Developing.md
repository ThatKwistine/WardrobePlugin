# Developing

## Building

1. Clone the repo next to a working Dalamud plugin dev setup
2. Ensure `DalamudLibPath` points to your Dalamud `dev` folder (default: `%AppData%\XIVLauncher\addon\Hooks\dev\`)
3. `dotnet build`
4. Copy the output DLL to your Dalamud dev plugin folder

Dalamud loads the plugin from `bin/Debug/…` for local development — that path is recorded in
`dalamudConfig.json`. Release exists only to produce `latest.zip`; building it does not change what
you are running in game, and vice versa.

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

## Releasing

See [RELEASING.md](../RELEASING.md).

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
