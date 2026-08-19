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

**Use whichever collection my character is on** is for anyone whose collection changes with the
character they are playing — Character Select+ and similar plugins give each character their own.
With it on, wearing and removing an item goes to the collection Penumbra is applying to you at that
moment, whatever collection the item was saved with. The saved collection stays on the item and is
used only when Penumbra has no answer, such as when you are not logged in. The setting shows which
collection that currently is, so you can check it against the character you are on.

Worn items follow the collection too. When it changes, the wardrobe re-reads what is actually
enabled and updates the ticks: items belonging to the character you just left stop showing as worn,
and items already enabled for the character you moved to start showing as worn. Nothing is enabled
or disabled by this — only the record of what is on is re-read, so the character you left keeps its
mods exactly as you left them, and going back re-ticks them.

That is also what keeps a removal honest. Taking an item off only ever turns mods off in the
collection the wardrobe turned them on in, so an item worn on another character is never quietly
removed from underneath that character while you are on this one.

The mods left enabled for the character you moved off are not left silent, though. The wardrobe
watches for the collection changing — a couple of seconds, and with the window shut too — and when
it does, tells you what it is still holding on elsewhere:

> **3 mod(s) the wardrobe enabled are still on in another collection.**
> **Disable Them** turns them off in the collection they are actually in.
> **Keep Them** leaves them alone, which is what you want if you swap back and forth.

Only mods the wardrobe itself switched on are ever listed or touched. Anything you enabled yourself
in Penumbra is not the wardrobe's to turn off, and never appears here.

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

### Newest installed first

**Newest installed first**, inside the mod picker when importing, lists Penumbra's mods with the most
recently installed at the top instead of A–Z. It is off by default — a list of several hundred mods is
searched by name far more often than it is browsed — and on when you have just downloaded something and
want to import it without knowing its exact name. The order the list is in is named beside **Mod**, and
the Mass Import window uses whichever you chose.

The date is when each mod's folder was created, which is when Penumbra imported it. Penumbra does keep
a proper import date, but only in its own database and with nothing exposed to read it from, so a folder
copied from somewhere else — a mod library moved to another drive — carries the date of the copy rather
than of the download. It is a way of finding what you just installed, not a record of when you got it.

Reading it costs one filesystem check per mod, so the order is worked out when a picker opens rather
than continuously.

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

## Glamourer Designs

**Show Glamourer designs as outfits** gives each of your Glamourer designs a card in the outfits grid.
It is the only setting the feature has, because the cards are a live link rather than a copy: a design
saved in Glamourer appears by itself, a rename follows through, and deleting it there removes the card.

Wearing one applies the design, then any wardrobe items attached to it — which is how the mods that
belong with a look get enabled, since a design knows nothing about Penumbra. Each card also takes
pictures, tags and styles like any other outfit, and shows the design's own pieces beside them. A design
that sets no gear — one saved for a face or a body — is labelled as such rather than shown empty.

Off by default: it is a visible change to a grid you have already arranged, and a Glamourer holding
sixty designs would otherwise become sixty cards. Turning it off hides the cards and keeps everything
attached to them, with a count shown while any are hidden.

The panel also reports cards whose design has been deleted in Glamourer while something of yours was
attached to them — those are kept rather than dropped. See
[Glamourer designs](Outfits.md#glamourer-designs).

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

## Base character

The character underneath the clothes: what a strip leaves on, and what a screenshot session puts back
before every shot.

Pick one from the list, or **New** to start another. Each holds **what a strip leaves alone**, the
**items** it wears, and a **Glamourer design** whose customisations it applies. Slots holding one of
its own items are ticked and locked — those are already protected on the item's behalf.

The slots come in two blocks. **Equipment slots** keep whatever is worn in them. **Character mods** —
hair, face, tail, Viera ears, skin, and shared textures like piercings — keep the mod you have on
switched on, which is the half a strip would otherwise take with it.

**Add worn customisation** takes every hair, face, tail, Viera ear, skin and shared-texture item you
have on right now, which is the quickest way in if you are already wearing your character.

**Apply base character now** puts it on without removing anything. The same picker is on the
screenshot session HUD. See [Base character](Wearing-Items.md#base-character).

The **Glamourer design** a base holds is a live link, never a copy. Only its ID is stored, and it is
handed to Glamourer at the moment of applying, so editing the design in Glamourer puts the edit in
the next apply — there is nothing to re-import and nothing to keep in step by hand. Two tick boxes
say how much of it is used and how often:

- **Apply the design's gear on every strip too** — off by default, so the design supplies only the
  face, body and colouring and a strip does not put the clothes back. On, the whole design is the
  base look, gear included. Disabled with a note when the design sets no equipment at all.
- **Keep this design on, not just on a strip** — off by default, applying the design when the base
  goes on: a strip, a screenshot session, a change of base, the button. On, it goes back after every
  redraw of your character too, so a Penumbra reload cannot take your face with it. That is what lets
  a base be nothing but a design, with no wardrobe items at all. Leave it off if you edit your
  character in Glamourer directly, or it will put the design back over what you were doing.

**Use this base when the collection is** ties a base to a Penumbra collection. It only appears when
**Use whichever collection my character is on** is ticked, since nothing watches the collection
otherwise, and it is not a control worth having for a collection that never changes. If your collection
changes with the character you are on, that is the same as tying it to a character: change to them,
and their base becomes the active one and is put on — the face, the ears, the skin, and any items
the base wears. A collection no base is bound to changes nothing, and leaves whichever base is active alone.

The link to the rest of that character's look is the base's own **Glamourer design**: point it at
the same design you have the character set up with, and the two travel together. It has to be
pointed by hand, once — Glamourer cannot be asked which design is currently applied, because a
design becomes ordinary character state the moment it lands, so no plugin can work out on its own
which one you are wearing.

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

**Your own icons** takes images named after their slots — `Head.png`, `Body.png`, `RingRight.png` —
from either an installed pack or a folder of your own. Any slot you supply uses your image; every
other slot stays on the set chosen above, so replacing two icons is as valid as supplying a full set
and nothing ever renders blank.

- **Icon pack** is the dropdown of installed packs. **Import zip…** unpacks a zipped set into the
  plugin's own folder and switches to it, **None** goes back to the built-in set without
  uninstalling anything, and the **x** beside a pack in the dropdown deletes it after a
  confirmation. **Copy path** puts the packs folder on the clipboard for editing or re-zipping one.
- **Your own folder** points at any folder and is layered *over* the pack, so a file there replaces
  that one slot and leaves the rest of the pack alone.
- Case does not matter, and most slots answer to more than one name. `.png`, `.webp`, `.jpg`,
  `.jpeg`, `.bmp` and `.tga` are accepted, and `.png` wins if a slot has more than one.
- Images are centre-cropped to a square rather than squashed, so they need not be square to start
  with.
- **Which slots** lists what was matched and, for each slot still missing, the file name it is
  looking for. Hovering a slot shows every name it will answer to. Worth opening if an icon does not
  appear — a misnamed file is otherwise invisible.
- **Rescan** re-reads both, for after adding or renaming a file while the game is running.
- **Clear** drops the folder and leaves any pack in place.

[Custom slot icons and icon packs](Custom-Icons.md) has the full name list, the image rules and how
to build a pack to share.

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

## Card Size

Two sliders, **Items** and **Outfits**, each from 0.7× to 2.5× and showing the resulting card size in
pixels. Ctrl-click to type a value; a **Reset** appears on either once it is off the default.

Separate because the two grids are looked at differently: an outfit preview is usually a full-body
shot and wants the room, while more item cards on screen is the point of the item grid. One slider
for both traded them against each other.

Both are also behind the magnifying glass on the toolbar, beside **Strip**, **Refresh** and **Scan** —
card size is judged by looking at the grid while dragging, which a settings panel four clicks away
cannot show you.

It scales the cards in **both** grids — items and outfits — so a wardrobe browsed by picture can have
big previews everywhere rather than only where a toggle exists. Larger cards mean fewer per row, and
names are cut to fit the card, so a bigger card shows more of the name rather than the same amount in
more space.

This is on top of Dalamud's Global Font Scale, not instead of it: cards still grow with your text
size, and this says how much larger than that you want them.

---

## Tag Colours

**Colour tags and styles** is on by default and changes nothing until you pick a colour. Right-click
a tag or style in the Tags panel for a colour picker with 0–255 R, G and B boxes; an item card then
takes the colour of its style. A worn item keeps its gold card whatever its style.

Turning it off keeps every colour already chosen and only stops them being used — including the
right-click picker itself, so the tag menu goes back to just **Delete**. See
[Tags](Tags.md#colours).

---

## Images Folder

Where the image browser looks for pictures to drag onto cards. See
[Images and screenshots](Images-and-Screenshots.md).

---

## Screenshots

The folder FFXIV saves screenshots to, which sessions watch for new files, plus three session
options:

- **Manual mode** keeps the session on each item for as many screenshots as you
  want to take, moving on only when you press **Next Item**. See
  [Manual mode](Images-and-Screenshots.md#manual-mode)
- **Strip other items before each shot** removes everything else the wardrobe has on, so only the
  queued item is showing
- **Compact main window during session** shrinks the plugin window to keep it out of the shot

All three persist between sessions. Stripping stops at your
[base character](Wearing-Items.md#base-character) if one is active, so the slots and customisation
mods you marked as part of the character survive every shot.

**Portrait outfit previews (9:16)** draws outfit previews as portraits rather than squares, matching
GPose's own portrait mode, and captures them that way. Outfit cards grow taller to suit. Item
previews stay square. Pictures already assigned are centre-cropped to whichever shape is in use, so
turning it on or off never spoils a wardrobe built under the other one — see
[Portrait outfit previews](Images-and-Screenshots.md#portrait-outfit-previews).

**Edit Angles For Every Slot** opens every slot's camera presets in one panel, for framing a whole
wardrobe's angles in one visit to GPose instead of inventing each one while a session waits. See
[Setting them up before a session](Images-and-Screenshots.md#setting-them-up-before-a-session).

---

## Changelog

**Show what changed after each update** opens a window listing what is new, once, the first time a
new version runs. It is on by default — the release page is where the notes live, and almost nobody
updating from the plugin installer ever sees it, so anything needing doing after an update was only
ever said out of sight.

Skipping two updates shows both sets of notes rather than only the newer one. A fresh install shows
nothing: the setup that follows is the introduction, and a list of changes to a version you never ran
is noise in front of it.

**View changelog** opens every version's notes at any time, newest first, whether or not the switch
above is on. Worth knowing about — the window closes on a click, and "what did that update say about
re-saving my presets?" tends to be asked the following day.

---

## Backups

**Enable hourly backups** copies the config to a folder of your choosing, at most once an hour and
only when the content hash differs from the previous backup, so an idle session produces no copies.

Files are named `WardrobePlugin_<timestamp>.json`, plus `CameraPresets_<timestamp>.json` if a preset
path is set. Anything past the keep-count is deleted oldest-first.

These are plain copies of the live config. Restoring means closing the game and putting the file
back over `%AppData%\XIVLauncher\pluginConfigs\WardrobePlugin.json`.

---

## Advanced dyes

**Keep advanced dyes with outfits** adds an **Advanced dyes** tick box to each item's dyes in an
outfit's edit panel, which stores Glamourer's colour-row edits for that piece and puts them back
whenever the outfit is worn. Glamourer stays the editor; the wardrobe only remembers. See
[Outfits and dyes](Outfits.md#advanced-dyes).

No longer experimental as of 1.5.1.1 — it has been used on real outfits — but still **off until you
turn it on**. Glamourer has no API for advanced dyes at all; they are carried inside its state data,
so this is the part most likely to break when Glamourer updates, and something reaching that far into
another plugin is worth choosing rather than having arrive in an update.

Turning it off keeps everything already captured — the rows stay on their outfits and return
when it is switched on again — but the wardrobe stops applying them, so those looks go back to plain
dyes. Nothing already on your character is undone at that moment: switching it off says stop touching
advanced dyes, and reverting them would be one more touch. Use Glamourer to clear those.

Capturing, re-capturing and clearing rows all happen per item in an outfit's edit panel — see
[Advanced dyes](Outfits.md#advanced-dyes). This section is only the switch.

---

## Setup

Reopens the first-run walkthrough. See [First run](#first-run) above.
