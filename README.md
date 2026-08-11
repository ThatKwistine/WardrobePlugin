<div align="center">

# Wardrobe

**A visual wardrobe for your Penumbra mods.**

Wear and unwear one piece at a time, without touching the rest of your look.

</div>

---

## What it does

Penumbra organises your mods. Glamourer sets your appearance. Neither gives you a way to see your
clothes and put them on.

Wardrobe does. Every entry is a single piece of gear with a picture, targeting a single equipment
slot. Click **Wear** and it enables the right Penumbra mod, picks the right options, and tells
Glamourer to equip the matching item — all of it, in the order that actually works. Click **Unequip**
and the slot goes empty.

- **One slot at a time.** Change your boots without disturbing anything else you have on.
- **Linked items.** Pieces that belong together go on and come off together, with a button for
  wearing just the one.
- **Outfits.** Save a whole look, wear it in one click, dye it in another.
- **Your glamour plates.** Sync the game's own plates in as read-only outfits, preview them anywhere
  through Glamourer, photograph them like anything else — or apply one for real, in game.
- **Pictures.** Drag images onto cards, or let the plugin take the screenshots for you.
- **Find things.** Nested tags, favourites, search across names and notes, sort by name or date.
- **Bulk import.** Bring in a batch of mods at once instead of one at a time.

It also handles the awkward cases: mods spanning several slots, mods needing an upscale or a
compatibility patch alongside them, hair and skin mods, colour variants of the same piece, and —
if you want them — animations, VFX and mounts.

---

## Requirements

| | |
|---|---|
| [**Penumbra**](https://github.com/xivdev/Penumbra) | Required. Does the mod file replacement. |
| [**Glamourer**](https://github.com/Ottermandias/Glamourer) | Required. Does the appearance overrides. |
| **Dalamud API 15** | The plugin framework. You have this if your other plugins work. |

The plugin tells you on first launch if either is missing.

---

## Installation

1. In game, open **Dalamud Settings** (`/xlsettings`)
2. Go to **Experimental → Custom Plugin Repositories**
3. Paste this URL into the empty box at the bottom and press the **+** button:

   ```
   https://raw.githubusercontent.com/ThatKwistine/WardrobePlugin/main/pluginmaster.json
   ```

4. Click **Save and Close**
5. Open the plugin installer (`/xlplugins`), search for **Wardrobe**, and install

Updates then arrive through the normal plugin installer like any other plugin.

---

## Getting started

Open the wardrobe with `/wardrobe` (or `/wr`) or the plugin icon. On a fresh install you get a short setup for
the settings that cause the most trouble when left unset — your Penumbra collection and your image
folders. All of it is skippable and editable later.

> **Set your collection to the one your character actually uses.** This is far and away the most
> common reason a wardrobe item appears to do nothing. Check **Collections → Your Character** in
> Penumbra.

### Adding your first item

1. Click **+ Import from Mod**
2. Pick a **collection**, then a **mod** — the search boxes filter long lists
3. Click **Analyse Mod** — the plugin reads the mod's files and works out which slots it covers
4. Tick the slot you want; the matching FFXIV item is detected automatically
5. Optionally add upscales or patches under **+ Add Supplementary Mod**
6. Click **Import**

Then open **Images** in the toolbar and drag a picture onto the new card — or use a
[screenshot session](docs/Images-and-Screenshots.md) and let the plugin do it.

### Importing a batch

**Mass Import** in the toolbar lists every mod in a collection at once, for when you are filling a
wardrobe rather than adding one piece. Tick what you want, optionally set each mod's options and
attach its supplementary mods, then click **Import Mods**. Items are named after the mod, and slots
you already have an item for are skipped.

Anything imported this way can be renamed, re-imaged and re-detected afterwards in the edit panel,
so it is worth being liberal here and tidying up later. See
[Items, mods and detection](docs/Items-and-Mods.md#mass-import) for the details.

---

## Commands

| Command | Effect |
|---|---|
| `/wardrobe` | Open the wardrobe window |
| `/wardrobe wear <name>` | Wear an item by exact name (case-insensitive) |
| `/wardrobe unequip` | Unequip all currently worn items |

`/wr` is a shorthand for `/wardrobe` and takes the same arguments.

---

## Documentation

| | |
|---|---|
| [Wearing items](docs/Wearing-Items.md) | How wear and unequip work, linked items, and why the order matters |
| [Outfits and dyes](docs/Outfits.md) | Saving looks, per-item dyes, editing an outfit by wearing it |
| [Items, mods and detection](docs/Items-and-Mods.md) | Mass import, supplementary mods, variants, shared models, option groups |
| [Tags](docs/Tags.md) | Sub-tags, making tags ahead of use, tagging a batch, filtering |
| [Images and screenshots](docs/Images-and-Screenshots.md) | Previews, automated sessions, camera presets |
| [Settings and backups](docs/Settings.md) | Every setting, and how backups work |
| [Custom slot icons](docs/Custom-Icons.md) | Naming icons, installing icon packs, building one to share |
| [**Troubleshooting**](docs/Troubleshooting.md) | When something doesn't show up |
| [Developing](docs/Developing.md) | Building from source and the IPC surface used |

---

## Something not working?

Start with [Troubleshooting](docs/Troubleshooting.md) — nine times out of ten it is the collection.

If that doesn't cover it, [open an issue](https://github.com/ThatKwistine/WardrobePlugin/issues) with
your Wardrobe, Penumbra and Glamourer versions and the relevant part of `/xllog`.

---

## Licence

Copyright (C) 2026 ThatKwistine

This program is free software: you can redistribute it and/or modify it under the terms of the
**GNU Affero General Public License** as published by the Free Software Foundation, either version 3
of the License, or (at your option) any later version. See [LICENSE](LICENSE) for the full text.

It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

AGPL-3.0 matches [Dalamud](https://github.com/goatcorp/Dalamud), which this plugin links against.
In practice it means you are free to use, study, modify and share it — and that anything built on it
and distributed onward stays open too.

The plugin icon is not covered by this licence.
