# Settings and Backups

Settings are reached from the toolbar, and the sections below are in the order they appear.

---

## First run

On a fresh install the window opens into a short six-step setup covering the things that cause the
most trouble when left unset:

1. **Intro**
2. **Which Penumbra collection does your character use?**
3. **What should the wardrobe hold?** Gear is always managed; animations, VFX and mounts are optional
4. **Where should item images live?**
5. **Where does FFXIV save your screenshots?**
6. **Keep backups of your wardrobe?**

Every step is skippable and everything is editable afterwards. Existing configurations skip the
walkthrough automatically, since anything already imported is taken as evidence the plugin is set
up.

**Setup → Run first-time setup again** reopens it. Each step starts from your current setting and
only what you change is changed. Wardrobe items and images are never touched.

---

## Collection

The collection new imports start in. **Set this to the collection your character actually uses.**

See [Troubleshooting](Troubleshooting.md#an-item-does-nothing-when-i-wear-it) for why this matters
more than anything else in the plugin.

---

## Importing

What the import panel's mod lists leave out.

- **Hide already-imported mods when importing** drops mods the wardrobe already covers
- **Hide support mods when importing** drops mods attached only as supplementary mods

With both on, the lists show only mods the wardrobe does not reference at all. Left off, those mods
still appear but greyed and annotated.

Both are also exposed as checkboxes at the top of the **Mass Import** window, where they matter most.
They are the same two settings, not copies — changing either one in either place changes it
everywhere.

---

## Other Mod Types

**Manage other mod types** adds Animation, VFX and Mount / Minion to the filter bar and to the slot
pickers when importing or editing. Animation covers every animation mod, not only emotes — idles,
poses, movement and battle animations all land there.

These have no game item to equip, so wearing one only enables its Penumbra mod and Glamourer is left
alone entirely. It is off by default because the extra buttons are noise to a wardrobe made only of
gear.

Turning it off hides items in those categories from the grid but keeps them saved, so turning it
back on restores them intact. A warning shows the count while any are hidden.

Animation mods that replace the same animation swap each other out, like two body mods do. Which
animation an item replaces is detected on import and can be changed when editing it.

---

## Wearing

**Switch hairstyle when applying hair mods.** Hair mods change how a hairstyle *looks*, not which
hairstyle your character has. With this on, the plugin also sets the hairstyle number the mod
expects.

**Revert customisation mods to** takes a Glamourer design holding your character's normal look.
Reverting a hair, face or skin item re-applies that design's *customisations only*, so equipment is
untouched. Without one, the plugin restores just the hairstyle number it noted when the item was
applied.

---

## Slot Icons

**Use icons for slots** replaces slot names with icons on the item cards and slot filter row,
choosing between FFXIV's own Character-window icons and Font Awesome. Both sets draw into the same
fixed square so switching never reflows the layout.

Hair, face, tail, Viera ears and skin have no game icon and always use Font Awesome. Every icon
carries a tooltip with the slot name, which is the only way to tell the two ring slots apart.

---

## Images Folder

Where the image browser looks for pictures to drag onto cards. See
[Images and screenshots](Images-and-Screenshots.md).

---

## Screenshots

The folder FFXIV saves screenshots to, which sessions watch for new files, plus two session
options:

- **Strip other items before each shot** removes everything else the wardrobe has on, so only the
  queued item is showing
- **Compact main window during session** shrinks the plugin window to keep it out of the shot

Both persist between sessions.

---

## Backups

**Enable hourly backups** copies the config to a folder of your choosing, at most once an hour and
only when the content hash differs from the previous backup, so an idle session produces no copies.

Files are named `WardrobePlugin_<timestamp>.json`, plus `CameraPresets_<timestamp>.json` if a preset
path is set. Anything past the keep-count is deleted oldest-first.

These are plain copies of the live config. Restoring means closing the game and putting the file
back over `%AppData%\XIVLauncher\pluginConfigs\WardrobePlugin.json`.

---

## Setup

Reopens the first-run walkthrough. See [First run](#first-run) above.
