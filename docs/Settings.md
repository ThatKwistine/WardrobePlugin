# Settings and Backups

## First run

On a fresh install the window opens into a short setup covering the settings that cause the most
trouble when left unset: your **Penumbra collection**, the **images** and **screenshots** folders,
and **backups**. Every step is skippable and everything is editable afterwards in Settings, where
**Run first-time setup again** reopens it.

Existing configurations skip this automatically — anything already imported or configured is taken as
evidence the plugin is already set up.

## Default collection

The collection new imports start in. **Set this to the collection your character actually uses.** See
[Troubleshooting](Troubleshooting.md#an-item-does-nothing-when-i-wear-it) for why this matters more
than anything else in the plugin.

## Backups

Written to the folder set in **Settings → Backups**, at most once an hour and only when the content
hash differs from the previous backup — so an idle session produces no copies.

Files are named `WardrobePlugin_<timestamp>.json` (plus `CameraPresets_<timestamp>.json` if a preset
path is set), and anything past the keep-count is deleted oldest-first.

These are plain copies of the live config. Restoring means closing the game and putting the file back
over `%AppData%\XIVLauncher\pluginConfigs\WardrobePlugin.json`.

## Mod list filtering

- **Hide already-imported mods** — drops mods the wardrobe already covers from the import list
- **Hide support mods** — drops mods attached only as supplementary mods

With both enabled the lists show only mods the wardrobe does not reference at all.

## Revert customisation mods to

A Glamourer design holding your character's normal look, used when unwearing a hair, face or skin
item. See [Customisation mods](Items-and-Mods.md#customisation-mods).

## Experimental

### Slot icons

Replaces slot names with icons on the item cards and slot filter row, choosing between FFXIV's own
Character-window slot icons and Font Awesome. Both sets draw into the same fixed square so switching
never reflows the layout.

Hair, face, tail, ears and skin have no game icon and always use Font Awesome. Every icon carries a
tooltip with the slot name, which is the only way to tell the two ring slots apart.
