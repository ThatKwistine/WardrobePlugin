# Items, Mods and Detection

## What a wardrobe item stores

- **Name** and optional **preview image**
- **Equipment slot** (Head / Body / Hands / Legs / Feet / Earrings / Neck / Wrists / Ring / Main Hand / Off Hand / Facewear)
- **Mods list** — collection name, mod directory, mod name, and saved option group selections for each mod
- **Detected game item** — the FFXIV item row ID and display name auto-detected from the mod's game file paths
- **Tags** — free-form labels for filtering
- **Notes** — free text, searched along with the name; a ¶ on the card means an item has some
- **Date added** — set on import, used for date sorting; hover an item's name to see it
- **Favourite flag** — toggled from the card, filterable from the ♥ button on the slot filter row

## Renaming from the card

**Double-click an item's name** on its card to rename it there, without opening the edit panel.
Enter commits, clicking away commits, Escape abandons it. A blank name is refused, and the old one
is kept.

This is mostly for what mass import leaves behind. Items are named after the mod, and a mod is named
by whoever packaged it — fifty hairstyles arrive carrying fifty creator watermarks, and trimming
those used to mean opening the edit panel fifty times for the field that sits at the top of it.

It is not offered in select mode, where a click on a card is a tick.

## The card menu

**Right-click anywhere on a card** for the quick edits: **Wear** or **Unequip** (with the linked pieces, and *only this* when there are any),
**Size** where the item has one, **Edit…**, **Rename**, **Select**, **Favourite**, **Put in
folder** and **Take out of folder**, **Tags** and **Styles** as tick lists with a box for a new tag,
**Screenshot**, **View picture**, **Copy to wardrobe**, and **Delete** (Ctrl-armed, like the X).

**Select** is the way into select mode from the card you want selected: it switches the mode on
with that card ticked, and reads **Deselect** on a card that already is.

In **Select** mode, right-clicking a ticked card acts on the whole selection — the menu says how many
— and the entries that only make sense for one card step aside. Right-clicking an unticked card acts
on that card alone.

## The edit panel

**Edit** on a card, or **Edit…** in its menu, opens the item in the panel on the right. The picture,
its gallery, **Take Screenshot**, the name, the image path and the slot are always on screen; the rest
is folded into sections under them:

- **Game item** — what the mod was detected as, **Re-detect**, and the pickers for overruling it
- **Glamourer design** — on customisation items only; see [A design applied with the item](#a-design-applied-with-the-item)
- **Mods** — each mod's collection, and **+ Add Supplementary Mod**
- **Mod Options** — every option group of every mod, read from disk when first opened
- **Tags**, **Notes**, **Linked items** and **Variants**

Sections with a list in them carry its count in their title — **Tags (3)**, **Linked items (2)** —
and **Notes** gains a dot when there is a note, so nothing has to be opened to find out it is empty.
Which sections are open is remembered, across items and across launches: open **Mod Options** once
and it stays open until you fold it again. Only **Tags** starts open.

An item on a gear slot with no game item detected says so in orange above the sections, whatever
is folded — it is the one state in there that needs acting on.

## Quick view

**View picture** on a card's right-click menu shows it full size, over the whole window — sized to
your screen rather than to the wardrobe window, which is usually the narrower of the two. It shows
the item's name, slot and tags, with **Wear**, **Edit** and **Close**; Escape closes it too. The
picture's tooltip says where to find it. Right-clicking the preview in an item's edit panel opens
the same viewer, and the panel also has a **View full size** button.

Outfits work the same way — right-click an outfit card's picture, or the preview in its edit panel.
The outfit version shows how many items it holds, any vanilla pieces, and its styles, with **Wear**
and **Edit**.

A menu entry rather than a button on the card: at small card sizes anything drawn over the thumbnail
covers the very thing it exists to show you. It used to be a right-click on the picture itself, until
the card grew a menu of its own and the two fought over the click.

An item or outfit with [several pictures](Images-and-Screenshots.md#several-pictures-per-item) shows
the count in the corner of its thumbnail, and the viewer pages through them with **◄ ►** or the arrow
keys.

## Deleting things

**Every control that takes something away needs Ctrl held.** They are greyed out until you hold it,
and their tooltip says so; hold Ctrl and they light up and work as normal. That covers deleting an
item, an outfit, a camera preset, a tag, a style or an icon pack, and equally the smaller removals —
taking an item out of an outfit, a tag off an item, a supplementary mod off an import. In context
menus the entry is greyed with **Ctrl** shown where a shortcut would be.

One rule with no exceptions, deliberately. These sit beside buttons you press constantly, and a rule
that applies to some of them is one nobody can predict. The bulk **Delete n item(s)** and removing an
icon pack keep their confirmation dialog *as well*.

Deleting a wardrobe item never touches the Penumbra mod behind it, and deleting an outfit never
touches the items in it.

## Importing

Mods you have already imported appear greyed out in the mod list, annotated with the slots they
cover. They stay selectable, because one mod often supplies several slots — after analysing, any slot
that would duplicate an existing item is pre-unchecked and marked, so a re-import only adds what is
genuinely new. **Settings → Hide already-imported mods** drops them from the list entirely instead.

Mods attached only as supplementary mods are shown greyed *and italic*, labelled `(support mod)`, and
can be hidden separately via **Settings → Hide support mods**. Italic needs a system italic font
(Segoe UI / Calibri / Arial italic); if none is found the text is simply greyed. With both hide
options enabled the lists show only mods the wardrobe does not reference at all.

All of this applies identically to the **+ Add Supplementary Mod** picker — both pickers render their
rows through the same code path.

The list is A–Z by default. **Newest installed first**, a checkbox inside the picker itself, puts what
you most recently installed at the top — the other way anyone looks for a mod, when you have just
downloaded something and do not know its exact name. The order in use is named beside **Mod**, and Mass
Import follows the same choice. See
[Newest installed first](Settings.md#newest-installed-first) for what the date actually measures.

A **Tags & Notes** section sits above the **Import** button. Whatever is set there is applied to
every item the import creates. **Mass Import** has a **Tags** button beside **Import Mods** doing the
same for a batch. See [Applying tags](Tags.md#applying-tags).

Option groups start from whatever Penumbra currently has active for the mod, rather than from each
group's first option. If you have already picked a body size or ticked a set of toggles in Penumbra,
the import arrives with those selected and you edit from there. It is a starting point only — nothing
stays in sync afterwards, so changing the mod in Penumbra later does not change the wardrobe item.

## Mass import

**Import → Mass Import** opens a window listing every mod in the chosen collection, for filling
a wardrobe rather than adding a single piece. Pick the collection first: each mod's options are read
from Penumbra when that mod is first analysed, and switching collection afterwards does not re-read
them.

Each row has a tick box, a **Set Mod Options** dropdown and a **+ Add Supplementary Mod** dropdown.
The tick box in the header selects everything currently listed, which respects the search box and the
two hide options — it never quietly selects rows you cannot see.

Setting a mod as somebody's supplement greys it out, removes its tick box, and moves its row directly
beneath its parent. Only that row moves; the rest of the list stays where it was. The row reads
*Set as supplemental mod for …* and carries an **x** to undo it, so you do not have to go hunting for
the parent to detach it. A supplement's own option groups appear inside its parent's **Set Mod
Options**, the same way they do when importing one mod at a time.

**Hide mods I have already imported** and **Hide supplementary mods** are the same two settings used
by the single-import picker, so changing one here changes it everywhere. A supplement is hidden or
shown with its parent rather than judged on its own, so you never see half of a configured pair.

The **Tags** button beside **Import Mods** applies a set of tags to everything the batch creates. It
carries a count once anything is set, so tags chosen and then dismissed are still visible before you
import. See [Applying tags](Tags.md#applying-tags).

Clicking **Import Mods** creates items using the same rules as a single import: one item per detected
slot, named after the mod with the slot in brackets when it produced more than one, and slots you
already have an item for are skipped. The window stays open afterwards so you can carry on down the
list — the batch tags stay set, so a second run down the list keeps them.

## Adding supplementary mods later

The edit panel's **Mods** section has its own **+ Add Supplementary Mod**, so an upscale or
compatibility patch you missed can be attached without deleting and re-importing the item. Existing supplements can be
removed there too; both take effect when you save, so **Cancel** discards them like any other edit.

Adding or removing a supplement applies to **every item built from the same mod**, not just the one
being edited — a mod covering body, legs and feet produces three items, and a body upscale belongs to
all three. **This includes variants**, which sit in the same slot as their source. The panel tells
you how many items will be affected before you save.

This is deliberately different from how *options* propagate. Options are allowed to differ between
same-slot variants, because Penumbra holds only one option state per mod and variants are never worn
together. Which mods are attached is a property of the mod files themselves, so it does not vary
between variants. A variant needing a different set of mods is really a separate item, not a variant.

## Slots contributed by supplementary mods

Slot detection covers the primary mod *and* every supplementary mod, so an upscale that adds legs to
a body-and-hands mod still offers Legs at import. Such slots are marked `· from supplement`. The slot
list rebuilds whenever supplementary mods are added, removed or swapped, preserving anything already
typed or chosen. Where both mods provide a model for the same slot, the primary mod's wins.

## Shared models

Many FFXIV items share one model — the Hakama legs model backs Asuran, Yasha, Yanxian, Nameless and
more. Detection picks the lowest row ID among them, which is arbitrary.

A model is also reused across patches, one recolour at a time. `e0110` is the Hellhound armour, the
Grey Hound armour and the Shadowhound armour: one model, three sets of materials, kept in `v0001`,
`v0002` and `v0003` folders beside it. A mod for one of those replaces that folder, so the wardrobe
reads the folder number out of the mod's files and offers only the items wearing it. Without that a
mod for the newest recolour was detected as the oldest — and wearing that item loads `v0001`, which
the mod does not replace, so the piece came out unmodded with nothing on screen saying why.

Only materials say which recolour. A mod shipping nothing but a model or textures narrows to nothing
and offers the whole family exactly as before, which is right: those files are shared by every
recolour of the set. Texture names carry a `v01_` prefix that looks like the same number and is not.

When more than one item shares the detected model, a **Game item** dropdown appears (per slot when
importing, and in the edit panel) listing every candidate so the intended one can be chosen. This
matters beyond cosmetics: the stored item ID is what Glamourer equips and what worn-detection
compares against.

## Facewear

Glasses, monocles and sunglasses are their own slot, called **Facewear** the way the game calls it.
They are worn alongside head gear rather than instead of it, so a hat and a pair of spectacles are
two wardrobe items and both can be on at once.

Detection has to work for them, because nothing in a facewear mod's paths says it is one: the models
sit in the equipment tree under the *head* suffix, so
`chara/equipment/e5501/model/c0101e5501_met.mdl` is a pair of oval spectacles and
`chara/equipment/e0110/model/c0101e0110_met.mdl` is a helm. The set number is the only difference,
and the game's own facewear list is what the wardrobe asks — sets 5501 to 5565 today, and whatever a
future patch adds without anything here needing to change. Before this, a glasses mod imported as a
hat with no game item behind it, and wearing it took your actual hat off to equip a piece that was
never there.

Two things about facewear differ from gear, both because the game says so:

- **It cannot be dyed.** The slot has no dye channel at all, so the pickers are replaced by a line
  saying as much rather than letting you set a colour that goes nowhere.
- **Its items are numbered separately.** Facewear comes from the game's `Glasses` list, not its item
  list, so the same number means a different thing in each. Everything that stores one stores the
  slot beside it.

Wearing facewear needs a Glamourer with the bonus-item API. It is there in 1.6.1.7; how far back it
goes has not been checked. Everything else in the wardrobe works with an older one — only facewear
stops, and it says so in the log rather than failing quietly.

## Setting the game item by hand

The **Game item** dropdown can only offer items that share the mod's detected model and recolour, so
it is no help when the right item shares neither with the mod — a piercing or a tattoo hung on an
Emperor's New piece because it is invisible, or a mod whose model or recolour was detected wrongly.

**Set game item manually** covers that. It is a collapsed section on each import slot row and under
**Game item** in the edit panel, searching every equippable item for that slot by name. **Clear game item** below it
leaves the item with none, so wearing it enables the mod but equips nothing and unequipping it leaves
the slot alone.

On import this writes to the row, so **Cancel** discards it along with the rest of the panel. In the
edit panel it saves immediately, like the dropdown above it.

## Re-detecting

If a mod is updated and its file paths change, open the item in the edit panel and click
**Re-detect**, under **Game item**, to re-run the analysis.

Items imported before the wardrobe read recolours have none recorded, and keep whatever game item was
detected for them at the time. **Re-detect** is what fills it in.

## Variants

**Create variant of this item** in the edit panel copies an item, inheriting its mods, collections,
options, detected game item and image, then opens the copy so you can change what differs.

Because variants occupy the same slot they are never worn at once, so each can hold its own option
selections. Items sharing a mod across *different* slots are worn together, and Penumbra holds only
one option state per mod — so those still share options, and editing one updates the others.

### Naming

**Settings → Variants → Name new variants** picks how the copy is named:

| Style | First two variants |
|---|---|
| Plain | `Silk Top (variant)`, `Silk Top (variant)` |
| Numbered | `Silk Top (Variant-1)`, `Silk Top (Variant-2)` |
| Lettered | `Silk Top (Variant-A)`, `Silk Top (Variant-B)` |
| When it was made | `Silk Top (07/08/26 - 21:45)` |

Plain is the default, because it is how variants have always been named. It gives every variant of
an item the same name. The numbered and lettered styles count on from the variants the item already
has, so they stay distinct however far apart the variants were created.

The timestamp style goes down to the minute, so two variants made within the same minute share a
name — made any further apart, each one is distinct.

The name is only where the copy starts — it opens for editing, with the name as its first field.
Changing the setting never renames variants you already have.

Names come from the group's *original*, not from whatever was copied, so making a variant of a
variant gives `Silk Top (Variant-3)` rather than `Silk Top (Variant-2) (Variant-2)`.

### Folding variants away

A variant belongs to the item it was copied from, and the grid folds it into that item's card rather
than giving it one of its own. The original's card carries a **+N** button showing how many are
tucked behind it; clicking it shows them, and **Fold N** puts them back. Any variant's card also has
a **Fold** button, so you do not have to find the original to close the group again.

Each group remembers whether you left it open. Groups you have never touched start folded, which is
the point — a wardrobe full of colour variants opens as one card per piece.

Two things are never folded away:

- **A variant that is worn.** Folding it would leave the original's card looking unworn while the
  variant it hid is what is actually on your character.
- **A variant the filters matched when its original did not.** Searching for a variant by name has
  to find it, even when the original does not match the search.

**Settings → Variants** turns the folding off entirely, and has a **Fold every group** button for
closing everything you have opened.

### Which items count as variants

Items created by **Create variant of this item** record where they came from, so their grouping is
exact. Groups are flat: a variant of a variant belongs to the same original, not to the copy it was
made from.

Items imported before that was recorded are grouped once, on first launch, by inferring it — items
in the same slot backed by exactly the same mods are taken to be the same piece in different
options, oldest first as the original. That is the same rule this page has always used to describe
what a variant *is*, but it is still a guess: two items you think of as separate will be grouped if
they share a slot and a mod set.

The edit panel's **Variants** section shows which group an item is in and has a button to leave it —
**Not a variant of this** on a variant, **Break this group up** on an original. Nothing is deleted;
the items simply get their own cards back. The inference only runs while no item has a recorded
group, so a grouping you break apart is not reassembled on the next launch.

Deleting an original does not scatter its variants — the oldest of them takes over as the head of
the group, and the group keeps whichever fold state it had.

### Finding them

A **Variants** button appears on the filter bar, beside **♥** and **Worn**, once anything is
grouped. It narrows the grid to pieces you have more than one version of — both the originals and
their variants. Folded groups still show as one card, so this reads as a list of the pieces rather
than of the copies. It combines with the search, slot and tag filters like every other filter.

The button is hidden entirely on a wardrobe with no variants, where it could only ever empty the
grid.

## Option group types

Penumbra groups are `Single`, `Multi`, `Imc`, or `Combining`. Only `Single` is a one-of-N dropdown —
the other three all store a bitmask and are shown as checkboxes.

`Imc` groups are easy to mistake for single-select because their type string isn't "Multi", but each
option carries a power-of-two `AttributeMask` and toggles independently. An unrecognised type is
logged as a warning rather than silently treated as a dropdown.

Checkbox groups carry **All** and **None** buttons beside the group's name, with a count of how many
are ticked — a mod whose toggles you want wholesale, or want to clear before picking two, does not
need clicking through one at a time. They act on that group only, never the mod as a whole, and are
hidden on a group with a single option. The same picker is used everywhere options are shown: the
import panel, an item's **Mod Options** section, and Mass Import.

### On, off, or leave it alone

In an item's **Mod Options** section, every checkbox option has three states rather than two:

| | Meaning |
| --- | --- |
| **✓** | Make sure this option is on when the item is worn |
| **•** | Leave it however it already is |
| **✕** | Make sure this option is off |

The middle state is what lets two items from the same mod be worn together without one undoing the
other. Penumbra holds one set of options per mod, so when a mod covers body and legs, both items
used to write the whole mod's settings and whichever was worn last won — which is why a body variant
would lose its options to the legs beside it. An item that leaves those options alone contributes
only what it cares about, and the two compound.

Bulk buttons match: **All on** and **Ignore all**, with a count reading `n on · n ignored · n off`.
**Ignore all** is the quick way to say a whole group belongs to some other slot's item.

Wardrobe now works this out for itself where it can. Each option group is checked against the files
it actually changes, so importing a mod that covers body and legs gives the body item the body's
groups and the legs item the legs'. A group that names no slot — meta edits, materials, shared
textures — is kept by both, since there is no honest way to attribute it. Groups belonging elsewhere
are labelled in the edit panel, so it is clear why they are being left alone.

### Groups you never set are left alone

An option group an item has never expressed a view on is not touched when that item is worn — not
turned off, and not set to its first option. That is what the item does, so it is now also what the
**Mod Options** panel shows.

It used to show something else. A checkbox group the item had no setting for was drawn with every
option unticked, and a dropdown it had no setting for was drawn on its first option, so opening the
panel once and saving handed the item opinions it never held. On gear that mostly passes unnoticed —
the groups an item does not tick are rival colours it would not want anyway. On a mod built by
**Loose Texture Compiler**, which is dozens of independent one-checkbox groups that do not compete,
it unticked every texture in the mod and left only the top one on.

If an item was saved that way before, those settings are real and are still honoured — nothing can
tell them apart from ones you chose. **Leave this mod's options alone**, at the top of that mod's
options, clears the lot in one press and takes effect when you save.

### What saving one item writes on another

Saving an item's options can also touch the other items from the same mod, and it is deliberately
narrow about it:

- **Never an item in the same slot.** Those are variants — different option sets for one mod, never
  worn at once — so they are always allowed to differ.
- **Only the groups the other item's slot is named in.** A group whose files name the legs is shared
  business between the body item and the legs item, because Penumbra holds one set of options per mod
  and both are worn at once. A group that names no slot is nobody's to hand out, and is left to each
  item's own choice.
- **Merged, not replaced.** Everything else the other item had set is left exactly as it was.

For a group two items really do share, set it to **Ignore all** on the one that does not care. That
is the item saying "this belongs to the other one", and nothing will overwrite it afterwards.

Dropdown groups get the middle state too, as a **• Leave alone** entry at the top of the list. They
hold one option at a time, so there is no per-option cross — "not this one" names nothing for
Penumbra to switch to — but the group as a whole can still be left out, which is the same idea one
level up. A dropdown set to leave alone is not written at all when the item is worn.

**Items saved before this existed keep their old behaviour** — their checkbox lists are still the
exact selection, turning off anything not listed. They gain the third state when you edit their
options and save, which is also what re-files the groups by slot. If two items from one mod are
fighting over a variant, editing and saving either one settles it.

**A group left entirely on • is remembered as left alone.** Earlier builds stored nothing for such a
group, which reads back the same as never having had one — so the panel showed it as all-off next
time, and saving again made that true. If you set groups to **Ignore all** before this was fixed,
set them once more and they will stay.

### Size options

Some option groups are not a style choice but a fit: a bust size, a thigh size, the group a body mod
ships so the same top sits right on two different bodies — "YAB+ S", "YAB+ M", "Bibo+". Those are
the options you change most often, and they can be put on the item's card so changing one is a
click there rather than a trip through Edit.

Under each group in an item's **Mod Options** section is a **Size option** tick. Ticked, the group
goes on the card as a small **Size: YAB+ M** under the buttons, and opening it lists the group's
options by the mod's own names — nothing is translated. Picking one sets the option here, exactly
as choosing it in Edit would, and if the item is on it is sent straight to Penumbra. See
[Sizes](Wearing-Items.md#sizes).

Under the tick, **On the card** lists the group's options, each with a tick for whether the card
offers it and a box for what the card calls it. A body mod can ship sixty sizes of which one
character wears four — untick the rest (**None**, then tick the four) and the card's pick is
those four; an option left off is still here, and still applies if it is the one the item is at.
A refit shipped as a mod of its own usually has a single toggle called "Top", which on the card
says nothing about what it is; type "Muse" in its box and that is what the card says. Blank keeps
the mod's own name. Options are always applied by their real names, and Penumbra is never changed
by either.

Most of this is ticked for you. Importing a body or legs item — and **Re-detect** on an older one —
looks for a group the slot has a say in with two or more options that plainly read as sizes
(*Small / Medium / Large*, *S / M / L*, *Flat / Big*, and the like) and marks it. It only ever
marks where nothing is marked, so a tick you took off stays off; and it can be turned off under
**Settings → Size Options**. A mod whose sizes are named unrecognisably is a tick away.

Checkbox groups work too, and a refit's single on/off toggle is the usual one. An item with
several size groups gets a **Size Options** button and a menu per group, and each group is its own
pick — choosing in one touches nothing in another. Within a checkbox group of several sizes,
picking one turns the other sizes off and leaves anything else in the group — a fix, an extra —
as it was.

A tick is per item and per slot, on purpose — the legs of a set mark the mod's leg group, not the
chest one — so saving it never writes onto the other items from the same mod, unlike the options
above. A copy made in another wardrobe keeps it. The whole feature can be switched off under
**Settings → Size Options**.

## Customisation mods

Hair, Face, Tail, Viera Ears and Skin replace part of the character model (`chara/human/…`) rather
than an equipped item, so they have no game item, no shared-model picker, and no "Emperor's New" to
strip to. Wearing one just enables its Penumbra mod and applies its options; unwearing disables it.

**Strip** deliberately leaves them alone, because it cannot remove a character's hair. It skips
animations, VFX and mounts for the same reason: stripping is about what the character has *on*, and
an animation is not worn.

Note this toggles how a hairstyle *looks*; it cannot switch which hairstyle your character has, as
Glamourer's IPC exposes no customisation setter (only `SetItem`, `SetBonusItem` and `SetMetaState`).
Pick the matching hairstyle in the character screen or Glamourer yourself.

### Layers: two mods on one slot

A face sculpt and a face retexture are both Face items, and they are not alternatives to each other —
the texture goes *on* the sculpt. The same is true of a body sculpt under a skin texture. Keying
customisation on the slot alone made every pair like this mutually exclusive: applying one took the
other off, with no way to have both.

Each customisation item therefore carries a **Layer**, shown on the import and edit panels:

- **sculpt** — the mod ships a model for that part, so it reshapes it.
- **texture** — it ships only materials and textures, so it repaints whatever is underneath.
- **blank** — the item takes the whole slot, displacing every other item in it.

It is detected on import from whether the mod contains a `.mdl` for the slot, and it is free text, so
a mod doing something with no obvious word for it can be given one — type `lashes` or `brows` into two
items and only those two will ever displace each other. Items sharing a layer still swap each other
out, which is what keeps two face sculpts behaving as the alternatives they are.

**Items imported before this existed have a blank layer**, and so still take the whole slot. That is
deliberate — filling them all in as independent would leave two sculpts enabled at once. Press
**Re-detect** in an item's edit panel to fill it in; the detection message says which layer it chose.
Moving an item to a different slot clears it, since a layer belongs to the slot it was read on.

#### Hair is never layered

Hair has no layer, and the field is not shown on a hair item. A character has exactly one hairstyle,
so two hair mods on at once is not a look anyone can wear — one of them is simply the mod for a
hairstyle you are not currently on, sitting enabled and invisible. Every hair item takes the whole
Hair slot, and applying one reverts whichever hair item was on before it.

It did carry a layer up to 1.6.0.0, and that was the bug this replaced: an older hair item had a
blank layer while a newly imported one was detected as `sculpt`, the two sat on different keys, and
neither could displace the other. The grid showed two hairstyles worn at the same time.

### A design applied with the item

A face sculpt replaces the files of one specific face number, so on a character set to any other face
it is enabled, correct, and completely invisible. Hair has never had this problem: the wardrobe reads
the hairstyle number out of the mod and switches you to it. A face has no single number to read — what
makes a sculpt look right is the face number *together with* the skin, eye and hair colouring around
it, and a Glamourer design is the thing that already holds all of that.

So a customisation item can name a **Glamourer design**, picked in the section of that name in its
edit panel, applied whenever
the item goes on. It is a live link and never a copy: only the design's id is stored, so editing it in
Glamourer puts the edit in the next apply, with nothing to re-import.

- **Apply now** applies the design's customisations once without wearing the item, for checking you
  picked the right one.
- **Apply its gear too** is off by default. A sculpt needs the face, body and colouring and nothing
  else — putting one wardrobe item on is not asking to be dressed. Turn it on where the design really
  is the whole character.
- **Apply its hairstyle too** is off by default as well. Every design carries a hairstyle whether or
  not it was saved for one, and a hair mod only replaces one hairstyle's files — so a design applied
  for its face would otherwise switch you off the hairstyle your hair mod needs and leave that mod
  enabled and invisible, which is the exact failure this whole feature exists to cure. Left off, the
  hairstyle in force is read before the design goes on and written back after, so a hair mod's number
  or your own survives either way. Turn it on where the design is the whole character.

**The same switch is on every design the wardrobe applies**: base characters, outfit design cards, and
**Settings → Revert customisation mods to**. There it is **on** by default, because that is what those
did before it existed — turn it off and a base or an outfit can supply a face and a body without
disturbing hair you are wearing. Only per-item designs default to off, since those are applied for a
face and have no business touching hair.

Reverting is the one place with an exception: taking a **hair** mod off always brings the revert
design's hairstyle with it, whatever the switch says. Without that you would be left standing on the
hairstyle the mod replaced, with the mod now disabled — which is exactly what reverting is for.

It goes on before the hairstyle step, so a hair mod's own number still wins over whatever hairstyle
the design carried, and it is put back after a redraw, so the base character's design cannot land on
top of it a frame later.

Taking the item off does not undo it by itself. **Settings → Revert customisation mods to** is what
puts your normal face back, and it already covers every customisation item — see
[Reverting customisation mods](#reverting-customisation-mods).

#### It warns when the design and the mod disagree

A customisation mod replaces the files of particular numbered variants — face 3, tail 2 — so a design
that sets any *other* number leaves the mod enabled, correct and invisible: the exact failure the
design is there to prevent, reintroduced by picking the wrong one. So the panel checks, and says so in
orange under the picker:

> ● Check this design
> This design sets face 3, and this mod replaces faces 1 and 101 on your race. It will be enabled and
> invisible.

The check is against every number the mod covers *for your race*, not one of them. Mods routinely
cover several at once — option groups let one mod ship f0001 through f0004, often for more than one
race — and a check against a single number would be wrong far more often than right. It also says so
when the mod has no files for your race at all, which no design can fix.

| Slot | Checked against |
|---|---|
| Face | the design's **face**, and your race |
| Tail | the design's **tail shape**, and your race |
| Viera ears | the design's **ear shape**, and your race |
| Skin | your race only |
| Hair | your race only |

Tail and Viera ears share one number because the game does: a single customisation is the tail on
races that have one and the ears on Viera. Skin has none to check — the body id in a skin mod's paths
is `b0001` for every player character — so only the race question applies, which is the failure skin
mods actually have. Hair has a number, but a design is not trusted to get it right: wearing a hair mod
sets the hairstyle from the mod itself, so only the race is checked there too.

It stays quiet whenever it cannot give an honest answer: a design that sets no such number, a
character whose race cannot be read, or an item whose coverage was never recorded. **Items imported
before this existed have no coverage recorded**, so they are never checked — press **Re-detect** and
the result line says what the mod replaces. Re-detect also re-reads it, so narrowing a mod's options in
Penumbra is picked up rather than vouched for by a stale answer.

The same warning goes to the log when the item is worn, since that is not a moment anyone is looking
at the edit panel.

### Redraw on apply

Switching a mod on redirects files, but it does not reload what is already drawn on your character.
Gear does not notice, because swapping the Glamourer item reloads the piece anyway — a hair, face or
skin mod has no item to swap, so it can be enabled perfectly correctly and still not appear until
something redraws you. An animation is the same story: the `.pap` your character is playing was
loaded long before the mod went on, so it keeps playing until you are rebuilt.

**Redraw on apply**, on the import panel and in the edit panel, does that redraw as the item goes on.
It is on by default for Hair, Face, Tail, Viera Ears, Skin, Other and Animation, and off for VFX and
Mount / Minion — those two are not on your character, so redrawing it does nothing for them. Items
imported before the toggle existed follow the same defaults; nothing needs re-importing.

Turn it off for a mod that shows up without it and you would rather not have the flicker. Removing an
item still redraws when nothing else would make it disappear, whichever way this is set.

### Reverting customisation mods

Set **Settings → Revert customisation mods to** to a Glamourer design holding your character's normal
look. Reverting a hair, face or skin item then re-applies that design's *customisations only* — its
equipment is ignored, so whatever the wardrobe currently has equipped is untouched.

Without a design set, the plugin falls back to restoring just the hairstyle number it noted when the
item was applied.

## Other mod types

**Settings → Manage other mod types** adds Animation, VFX and Mount / Minion to the filter bar and to
the slot pickers. These have no game item to equip, so wearing one only enables its Penumbra mod —
Glamourer is left alone entirely.

**Animation** covers every animation mod, not only emotes. Detection is by `.pap` file, which is the
only place animation data lives, so idles, poses, movement, sitting and battle animations all land
in the same category. If you want them separated further, use tags.

It is off by default because the extra filter buttons and slot-picker entries are noise to a wardrobe
made only of gear. Items already imported into those categories are kept but hidden from the grid
while it is off, so turning it back on restores them intact.

Animation mods that replace the same animation swap each other out, like two body mods do. Which
animation an item replaces is detected on import and can be changed when editing it.

**Strip** leaves these running, but **removing an outfit takes them off**, because wearing the
outfit is what enabled them in the first place.
