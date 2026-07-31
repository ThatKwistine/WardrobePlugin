# Wardrobe Plugin

A Dalamud plugin for managing individual clothing items across **Penumbra** mods and **Glamourer**. Each wardrobe entry represents a single equipment slot — enabling or disabling exactly what you need without touching the rest of your look.

---

## Requirements

| Plugin | Why |
|---|---|
| [Penumbra](https://github.com/xivdev/Penumbra) | Mod file replacement — IPC V5 used to enable/configure mods |
| [Glamourer](https://github.com/Ottermandias/Glamourer) | Appearance overrides — `SetItem.V3` used to push per-slot items |
| Dalamud API level 15 | Plugin framework |

---

## Features

- **Per-slot wardrobe items** — each entry targets one equipment slot (head, body, hands, legs, feet, ears, neck, wrists, rings, weapons)
- **Customisation mods** — hair, face, tail, Viera ears and skin mods are detected and managed the same way as gear
- **Auto-detected game items** — the plugin analyses the mod's file paths to identify which FFXIV item it replaces; Glamourer is updated automatically on wear
- **Multiple mods per item** — stack a primary mod with upscales, compatibility patches, etc.
- **Favourites** — mark items with the ♥ on their card and filter the grid down to just those
- **Tags** — label items and filter the grid by tag; click a suggested tag to add it, right-click to load it into the box and edit it first
- **Hourly backups** — copies the config to a folder of your choosing, skipping the write when nothing has changed
- **Search and sort** — free-text search across item name, tags, mod names, and the detected game item; sort by name (A–Z / Z–A) or date added (newest / oldest)
- **Image browser** — point the plugin at a folder of images and drag a thumbnail onto any item card to assign it as the preview
- **Screenshot sessions** — queue every item that has no preview image, equip each in turn, and pick up new screenshots from a watched folder automatically; optional per-slot GPose camera presets, a persisted "strip other items" mode, and a compact window mode that keeps the UI out of the shot
- **Worn-item detection** — cross-references Penumbra's enabled mods and option selections against Glamourer's equipped items to work out what is already worn
- **Clean unequip** — removing an item sets that Glamourer slot to the matching Emperor's New item so the slot appears empty

---

## What a wardrobe item stores

- **Name** and optional **preview image**
- **Equipment slot** (Head / Body / Hands / Legs / Feet / Ears / Neck / Wrists / Ring / Main Hand / Off Hand)
- **Mods list** — collection name, mod directory, mod name, and saved option group selections for each mod
- **Detected game item** — the FFXIV item row ID and display name auto-detected from the mod's game file paths (can be re-detected after a mod update)
- **Tags** — free-form labels for filtering
- **Date added** — set on import, used for date sorting; hover an item's name to see it
- **Favourite flag** — toggled from the card, filterable from the ♥ button on the slot filter row

---

## Adding an item

1. Open the wardrobe: `/wardrobe` or click the plugin icon
2. Click **+ Import Item** in the toolbar to open the import panel
3. Pick a **collection** and then a **mod** from the dropdowns — use the search boxes to filter long lists (the collection is pre-selected from **Settings → Default Collection**)
4. Click **Analyse Mod** — the plugin scans the mod's file paths and lists detected equipment slots
5. Select the slot you want to import; the detected game item is shown automatically

Mods you have already imported appear greyed out in the mod list, annotated with the slots they cover. They stay selectable, because one mod often supplies several slots — after analysing, any slot that would duplicate an existing item is pre-unchecked and marked, so a re-import only adds what is genuinely new. **Settings → Hide already-imported mods** drops them from the list entirely instead.

Mods attached only as supplementary mods are shown greyed *and italic*, labelled `(support mod)`, and can be hidden separately via **Settings → Hide support mods**. Italic needs a system italic font (Segoe UI / Calibri / Arial italic); if none is found the text is simply greyed. With both hide options enabled the lists show only mods the wardrobe does not reference at all.

All of this applies identically to the **+ Add Supplementary Mod** picker — both pickers render their rows through the same code path.
6. Optionally add supplemental mods (upscales, patches) via **+ Add Supplemental Mod**
7. Click **Import** — the item appears in the grid

To assign an image: open **Images** in the toolbar, browse to your images folder in Settings, then drag a thumbnail onto the item card.

---

## Wearing an item

Click the **Wear** button on any card, or use the command:

```
/wardrobe wear <item name>
```

**What happens:**
1. If something is already worn in that slot it is unequipped first
2. Glamourer's `SetItem.V3` is called *before* any mod is touched
3. For each mod: the mod is **enabled first**, then its option groups are applied
4. `SetItem.V3` is called again, in case step 1 overwrote the slot
5. If any mod was newly enabled, the item is re-applied a few times over the next several seconds

Multiple items can be worn simultaneously as long as they occupy different slots. Two items sharing the same underlying mod are handled correctly — the mod stays enabled until all items using it are unequipped.

**Why the order matters.** Enabling a mod makes Penumbra start an async resource reload, and Glamourer snapshots its state when that reload begins, re-applying the snapshot when it finishes. Calling `SetItem` first puts the right item in that snapshot. Enabling the mod before applying its options matters for the same reason — options set on a disabled mod are not picked up by the initial enable-reload, whereas changing an option on an already-enabled mod triggers a fresh reload that reads the correct state. The delayed re-applies (350 ms / 750 ms / 1800 ms / 4500 ms) exist because a mod with missing material files can keep reloading for several seconds, and each reload can undo the Glamourer state.

---

## Unequipping

Click **Unequip** on a worn card (shown with a filled circle), or:

```
/wardrobe unequip
```

**What happens:**
1. The Penumbra mod is disabled (unless another worn item still needs it)
2. Glamourer's `SetItem.V3` is called with the Emperor's New item for that slot, making it appear empty

---

## Commands

| Command | Effect |
|---|---|
| `/wardrobe` | Open the wardrobe window |
| `/wardrobe wear <name>` | Wear an item by exact name (case-insensitive) |
| `/wardrobe unequip` | Unequip all currently worn items |

---

## Building

1. Clone the repo next to a working Dalamud plugin dev setup
2. Ensure `DalamudLibPath` points to your Dalamud `dev` folder (default: `%AppData%\XIVLauncher\addon\Hooks\dev\`)
3. `dotnet build`
4. Copy the output DLL to your Dalamud dev plugin folder

---

## IPC surface used

### Penumbra (V5)
- `Penumbra.GetCollections.V5` — list available collections
- `Penumbra.GetModList` — list all installed mods
- `Penumbra.GetCurrentModSettings.V5` — read a mod's current enabled state and option selections
- `Penumbra.TrySetMod.V5` — enable or disable a mod in a collection
- `Penumbra.TrySetModSetting.V5` — set a **Single**-select option group (one option)
- `Penumbra.TrySetModSettings.V5` — set a **Multi**-select option group (full list at once)
- `Penumbra.GetModDirectory` — get the Penumbra mods root folder path
- `Penumbra.RedrawObject.V5` — force a redraw of the local player
- `Penumbra.GameObjectRedrawn.V3` — event fired after a redraw completes; used to re-apply Glamourer state

> **Single vs Multi is not interchangeable.** `TrySetModSetting.V5` sets a group *to* the option you
> pass — on a Multi group that **replaces** the whole selection rather than adding to it. Looping it
> over a checkbox group leaves only one box ticked, and if you cache the group's prior state before
> the loop it will oscillate between options on successive applies while still returning `Success`
> every time. Always use the plural `TrySetModSettings.V5` for Multi groups.

### Glamourer
- `Glamourer.SetItem.V3` — equip a specific FFXIV item in one slot on the local player
- `Glamourer.GetState` — read the full state object; used to detect what is currently equipped per slot
- `Glamourer.SetMetaState` — toggle metadata state (visor, hat visibility, etc.)
- `Glamourer.GetStateBase64` / `Glamourer.ApplyState` / `Glamourer.RevertState` — legacy outfit system (retained for backwards compatibility)

All IPC calls are wrapped in try/catch; failures are logged as warnings rather than crashing.

`Penumbra.Api.xml` ships next to `Penumbra.dll` in the installed plugin folder and is the
authoritative reference for these labels and signatures — check it before assuming call semantics.

---

## Notes

- **Customisation mods work differently to gear.** Hair, Face, Tail, Ears (Viera) and Skin replace part of the character model (`chara/human/…`) rather than an equipped item, so they have no game item, no shared-model picker, and no "Emperor's New" to strip to. Wearing one just enables its Penumbra mod and applies its options; unwearing disables it. **Strip** deliberately leaves them alone — it cannot remove a character's hair. Note this toggles how a hairstyle *looks*; it cannot switch which hairstyle your character has, as Glamourer's IPC exposes no customisation setter (only `SetItem`, `SetBonusItem` and `SetMetaState`). Pick the matching hairstyle in the character screen or Glamourer yourself.
- **Slots contributed by supplementary mods.** Slot detection covers the primary mod *and* every supplementary mod, so an upscale that adds legs to a body-and-hands mod still offers Legs at import. Such slots are marked `· from supplement`. The slot list rebuilds whenever supplementary mods are added, removed or swapped, preserving anything already typed or chosen. Where both mods provide a model for the same slot, the primary mod's wins.
- **Shared models.** Many FFXIV items share one model — the Hakama legs model backs Asuran, Yasha, Yanxian, Nameless and more. Detection picks the lowest row ID among them, which is arbitrary. When more than one item shares the detected model, a **Game item** dropdown appears (per slot when importing, and in the edit panel) listing every candidate so the intended one can be chosen. This matters beyond cosmetics: the stored item ID is what Glamourer equips and what worn-detection compares against.
- **Re-detecting items:** if a mod is updated and its file paths change, open the item in the edit panel and click **Re-detect** to re-run the analysis.
- **Stains:** `SetItem.V3` is called with stains `[0, 0]` (no dye). Dye state is whatever was last applied in Glamourer.
- **`applyFlags = 0`** on `SetItem.V3` — this is the persistent mode that goes through Glamourer's state machine rather than a one-shot apply.
- **The collection must be the one applied to your character.** This is the single most common reason a wardrobe item appears to do nothing. Penumbra enables the mod exactly as asked, reports success, and logs nothing unusual — but if that collection is not the one affecting your character, nothing shows up. In Penumbra, look at **Collections → Your Character** to see which collection applies, and note that an **Individual Assignment** for that character overrules it. Set **Settings → Default Collection** to the same one so imports start there.
- **Keep an item's mods in one collection.** A mod is only visible on your character if it is enabled in the collection that character actually uses. If an item's main and supplementary mods end up in different collections, every mod still enables successfully and the log looks clean, but only some of them show up. Set **Settings → Default Collection** to the collection your character uses so imports start there. Existing items can be corrected under **Mods & Collections** in the edit panel, which flags a split and warns on wear. Changing a mod's collection there also moves every other item that referenced the same mod in the same old collection, so a mistake repeated across items only needs fixing once.
- **Option group types.** Penumbra groups are `Single`, `Multi`, `Imc`, or `Combining`. Only `Single` is a one-of-N dropdown — the other three all store a bitmask and are shown as checkboxes. `Imc` groups are easy to mistake for single-select because their type string isn't "Multi", but each option carries a power-of-two `AttributeMask` and toggles independently. An unrecognised type is logged as a warning rather than silently treated as a dropdown.
- **Detection requires matching options.** An item only registers as worn if its mods are enabled *and* their option selections — single- and multi-select alike — match what the item saved. Two items sharing a mod but wanting different checkboxes will not both register.
- **Missing material warnings.** `Failed to asynchronously load resource ... .mtrl` in the log means the mod does not include that file for your character's race/gender. It is a gap in the mod, not a plugin fault, though it does make Penumbra reload repeatedly — which is what the delayed re-applies are there to survive.
- **Backups** are written to **Settings → Backups**, at most once an hour and only when the content hash differs from the previous backup, so an idle session produces no copies. Files are named `WardrobePlugin_<timestamp>.json` (plus `CameraPresets_<timestamp>.json` if a preset path is set), and anything past the keep-count is deleted oldest-first. These are plain copies of the live config — restoring means closing the game and putting the file back over `%AppData%\XIVLauncher\pluginConfigs\WardrobePlugin.json`.
- **Camera presets** are written directly to the game's camera and re-applied for about half a second, because a single write is overwritten by the game's own per-frame camera update. They only work with the native GPose camera — if BRIO's free camera is active it drives the camera independently and presets will have no visible effect.
- **Slot icons** (experimental) — **Settings → Experimental → Use icons for slots** replaces slot names with icons on the item cards and slot filter row, choosing between FFXIV's own Character-window slot icons and Font Awesome. Both sets draw into the same fixed square so switching never reflows the layout. Hair, face, tail, ears and skin have no game icon and always use Font Awesome. Every icon carries a tooltip with the slot name, which is the only way to tell the two ring slots apart.
- **Wardrobe sharing** (`WardrobeShareService`) — WebSocket hosting and the visitor allowlist — sits behind **Settings → Experimental → Enable experimental features**, and is hidden by default. It is scaffolded but has no backend yet, so connecting is currently a no-op.
