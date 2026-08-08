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

## Variants

**Name new variants** chooses what **Create variant of this item** calls the copy — a plain
`(variant)` suffix, numbered, lettered, or the date and time it was made. The panel shows what the
first two variants of an item would be called, and warns if a style repeats itself. Only the
starting name; the copy opens for editing, and existing variants are never renamed. See
[Naming](Items-and-Mods.md#naming).

**Group variants under their original** folds an item's variants into its card, leaving a **+N**
button that shows how many are behind it. Turn it off to give every variant a card of its own, as it
was before grouping existed.

**Fold every group** closes every group you have expanded. Individual groups remember whether you
left them open, so this is the one switch that resets all of them at once.

A variant that is currently worn is never folded away, whatever these settings say. See
[Folding variants away](Items-and-Mods.md#folding-variants-away).

---

## Slot Icons

**Use icons for slots** replaces slot names with icons on the item cards and slot filter row,
choosing between FFXIV's own Character-window icons and Font Awesome. Both sets draw into the same
square so switching never reflows the layout.

Hair, face, tail, Viera ears and skin have no game icon and always use Font Awesome. Every icon
carries a tooltip with the slot name, which is the only way to tell the two ring slots apart.

**Your own icons** points at a folder of images named after their slots — `Head.png`, `Body.png`,
`RingRight.png`. Any slot you supply uses your image; every other slot stays on the set chosen above,
so replacing two icons is as valid as supplying a full set and nothing ever renders blank.

- Either name works: the slot's own name (`RingRight`) or its label without spaces (`RightRing`),
  in any case. `.png`, `.webp`, `.jpg`, `.jpeg`, `.bmp` and `.tga` are accepted, and `.png` wins if a
  slot has more than one.
- Images are centre-cropped to a square rather than squashed, so they need not be square to start
  with.
- **Which slots** lists what was matched and, for each slot still missing, the file name it is
  looking for. Worth opening if an icon does not appear — a misnamed file is otherwise invisible.
- **Rescan** re-reads the folder, for after adding or renaming a file while the game is running.
- **Clear** drops the folder and every icon reverts.

Custom icons are sized by the same sliders below and scale with Dalamud's Global Font Scale like
everything else.

**Icon size** has two sliders, both showing the resulting size in pixels. Drag them, or ctrl-click to
type a value. A **Reset** button appears once either is off the default.

- **Cards** sizes the icon on each item card. Cards grow taller to fit a larger one, so nothing is
  pushed off the bottom edge — the grid re-flows to match.
- **Filter row** sizes the slot buttons along the top. Smaller icons fit more slots on the row
  before the rest spill into **More**, which is the main reason to change it. Everything else on
  that row — **Outfits**, **All**, **♥**, **Worn**, **Variants**, the **More** dropdown and the
  search and sort boxes — grows to the same height, so the row stays level.

They are separate because they trade against different things: the row is about how many slots fit,
the cards are only about legibility. The preview underneath shows the card size.

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
