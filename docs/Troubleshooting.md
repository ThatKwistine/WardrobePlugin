# Troubleshooting

## An item does nothing when I wear it

**This is the single most common problem, and it is almost always the collection.**

Penumbra enables the mod exactly as asked, reports success, and logs nothing unusual — but if that
collection is not the one affecting your character, nothing shows up.

1. In Penumbra, look at **Collections → Your Character** to see which collection applies.
2. Note that an **Individual Assignment** for that character overrules it.
3. Set **Wardrobe → Settings → Default Collection** to the same one so future imports start there.
4. For an item already imported, fix it under **Mods & Collections** in its edit panel.

If your collection changes with the character you are on — Character Select+ and similar plugins
assign one per character — tick **Settings → Collection → Use whichever collection my character is
on** instead of setting one. Items then apply to whichever collection Penumbra is using for you at
the time, so a wardrobe built on one character works on all of them.

## Only some of an item's mods show up

A mod is only visible on your character if it is enabled in the collection that character actually
uses. If an item's main and supplementary mods end up in different collections, every mod still
enables successfully and the log looks clean, but only some of them appear.

The edit panel flags a split like this and warns on wear. Changing a mod's collection there also
moves every other item that referenced the same mod in the same old collection, so a mistake repeated
across items only needs fixing once.

## Removing an item left its mod enabled in Penumbra

The wardrobe only turns off mods it turned on. If a mod was already enabled when you wore the item —
because a Glamourer design uses it, or you enabled it yourself — removing the item leaves it alone,
so whatever else depends on it keeps working. Turn it off in Penumbra when you want it off.

If you use **Use whichever collection my character is on**, there is a second way this happens: the
mod is enabled in the collection of the character you wore it on, and you have since changed
character. Removing the item works in the collection you are on now, where it was never enabled, so
it has nothing to turn off. The wardrobe notices the change and offers those mods under **Disable
Them**; **Keep Them** is the right answer if you are going back to that character.

Mods enabled by a version older than this one are not recognised as the wardrobe's either, since
there was nothing tracking that at the time. Those stay on once and are best cleared with **Disable
Their Mods** on the leftovers notice; after that they are enabled and disabled normally.

## A hair mod puts on the wrong hairstyle, or none

The wardrobe switches you to the hairstyle a hair mod replaces, working that out from the mod's files.
It reads the **model** to decide, because that is the one file that can only belong to the hairstyle
being replaced — hair mods routinely ship textures belonging to other hairstyles alongside it, and an
earlier version could take the number from one of those. The result was a mod enabled correctly with
your hair unchanged, since you had been switched to a hairstyle it does not touch.

If an item was imported before that fix, press **Re-detect** in its edit panel: it re-reads the mod and
stores the right numbers. The log line `'<item>': hairstyle N for race code NNNN` says which one it is
using, so it can be checked against the mod's own description.

## A hair, face or skin mod is enabled but nothing changed

Enabling a mod does not reload what is already drawn on you, so a mod that replaces part of the
character model can go on correctly and only appear the next time something redraws you — which is
why redrawing by hand in Penumbra "fixes" it.

**Redraw on apply**, in the item's edit panel, does that redraw for you. It is on by default for
customisation slots, so check it has not been turned off for that item.

If it is on and the mod still does not appear, the mod is probably built for a different face,
hairstyle or race than your character has. A face paint made for Face 1 shows nothing on Face 5.
Match your character to the mod in Glamourer — a [base character](Wearing-Items.md#base-character) is
a good place to keep that — or use a version of the mod built for what you have.

## An item is enabled in Penumbra but not showing on my character

The wardrobe flags these as desynced. It usually follows a crash or a manual change made in Penumbra
directly. Use the desync recovery prompt rather than **Unequip All**, which would otherwise have
nothing to unequip while the mod stays on.

## Two items sharing a mod won't both register as worn

An item only registers as worn if its mods are enabled *and* their option selections match what the
item saved. Two items sharing a mod but wanting different checkboxes cannot both be worn.

If they occupy the same slot, make them [variants](Items-and-Mods.md#variants) of each other instead.

## `Failed to asynchronously load resource ... .mtrl` in the log

The mod does not include that file for your character's race/gender. It is a gap in the mod, not a
plugin fault — though it does make Penumbra reload repeatedly, which is what the plugin's delayed
re-applies are there to survive.

## The wrong item was detected

Many FFXIV items share one model, and detection picks the lowest row ID among them. Use the **Game
item** dropdown to choose the one you meant — it is on each slot row while importing as well as in
the edit panel, so this can be fixed before the item is created. See
[Shared models](Items-and-Mods.md#shared-models).

The dropdown only appears when more than one item shares the model, and can only list items that do.
If the one you want is not there, use **Set game item manually** to search the slot's items by name —
also available in both places. See
[Setting the game item by hand](Items-and-Mods.md#setting-the-game-item-by-hand).

If a mod was updated and its file paths changed, click **Re-detect** instead.

## Camera presets do nothing

They only work with the native GPose camera. If BRIO's free camera is active it drives the camera
independently. Turn BRIO's camera off and try again.

## A screenshot session used the wrong camera preset

A slot can hold several, and a session applies whichever one has its **radio button ticked**. Tick
the one you want in the item's edit panel.

## Applying a preset does not restore the pan

Presets saved before pan was supported did not record one, and they deliberately leave the camera's
pan alone rather than guessing at a centred value. Press **Update** on the preset to capture it.

## Mod names show as boxes or missing glyphs

Some characters are missing from Dalamud's default font. The plugin substitutes replacements where it
can. Italic text in the mod pickers needs a system italic font (Segoe UI / Calibri / Arial italic);
without one the text is simply greyed instead.

## Restoring a backup

Backups are plain copies of the live config. Close the game, then put the file back over
`%AppData%\XIVLauncher\pluginConfigs\WardrobePlugin.json`.

Files are named `WardrobePlugin_<timestamp>.json`, plus `CameraPresets_<timestamp>.json` if a preset
path is set.

## Still stuck?

Open an issue at
[github.com/ThatKwistine/WardrobePlugin/issues](https://github.com/ThatKwistine/WardrobePlugin/issues).

Please include:

- your Wardrobe version (shown in the plugin installer)
- your Penumbra and Glamourer versions
- the relevant part of `/xllog`
