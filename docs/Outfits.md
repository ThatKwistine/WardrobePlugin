# Outfits and Dyes

**Outfits** sits at the start of the slot filter row and switches the grid to your saved outfits.

Wear a look you like, type a name, and **Save current look** records every item the wardrobe has on.

## Outfit cards

| Button | Effect |
|---|---|
| **Wear** | Wears the outfit's items, leaving anything else you have on in place |
| **Only this** | Wears them and removes everything else the wardrobe has on |
| **Update** | Replaces **Only this** while the outfit is on; saves what you are wearing now |
| **Remove** | Takes the outfit's items back off |
| **Photo** | Wears the outfit alone and waits for a screenshot, assigning it as the outfit's image |
| **Edit** | Opens the outfit for renaming, previews, and managing its contents |

Outfit cards start the same size as item cards, with their own slider behind the magnifying glass on
the toolbar — worth turning up if your previews are full-body shots showing too little. See
[Card size](Settings.md#card-size).

**Right-click an outfit's picture** to see it full size, on a card or in its edit panel. See
[Quick view](Items-and-Mods.md#quick-view).

## The edit panel

Every item in the outfit is listed as a row with a small thumbnail, its name and slot, and:

- an **Equip / Unequip** toggle so pieces can be tried individually
- a **+ Dye** button that equips the piece with its configured dyes without wearing the rest
- **Edit**, which opens that item's own panel — mod options, tags, image, slot. Clicking the item's
  name does the same. Closing it comes straight back to the outfit, still open on the same one, so
  adjusting a mod option mid-outfit is a round trip rather than a detour.
- **×**, which takes the item out of the outfit. The item itself is kept. Ctrl-guarded like every
  other removal — see [Deleting things](Items-and-Mods.md#deleting-things).

An **Add to outfit** picker at the bottom adds any wardrobe item not already in it, searchable by name.

## Duplicating an outfit

**Duplicate This Outfit**, above Save in the edit panel, makes a copy and opens it. **Right-click
Edit** on any card does the same from the grid.

The copy takes everything: items, dyes — advanced dye rows included — vanilla pieces, tags and the
preview image. The original is left exactly as it is.

This is how a variant starts. Three versions of the same look with one piece swapped, or the same
set dyed three ways, begin as three copies rather than as the same outfit built three times.

Names do not collide: the first copy of *Beach Day* is *Beach Day (copy)*, the next is *Beach Day
(copy 2)*, and so on. A grid of cards all called the same thing would be useless at exactly the
point the feature started working.

This is for outfits you made in the wardrobe. A [glamour plate](#vanilla-glamour-plates) has one
copy option instead — **Duplicate As Editable Outfit** — and it is the only one offered, from the
plate's panel or by right-clicking its card.

There is no plain copy of a plate because there is nothing sensible for one to be. A plate outfit is
a mirror of plate N; a second mirror of the same plate would leave two cards claiming it, and only
one of them would ever be resynced.

## Dyes

Each row carries **Dye 1** and **Dye 2** pickers, listing the game's dyes with a colour swatch and a
search box.

Dyes are stored on the **outfit**, not the item, so the same piece can be dyed differently in
different outfits. They are applied through Glamourer when the outfit is worn, and re-applied after a
redraw — otherwise a Penumbra reload would strip the colour back to undyed.

Customisation items cannot be dyed and say so.

Above the list, a **Dye all items** pair of pickers sets either channel across every dyeable item in
the outfit at once, for the common case of one dye through a whole set. Individual rows can then be
changed afterwards; once they disagree, the picker at the top reads **Mixed**.

Items worn on their own, outside an outfit, are always applied undyed.

### Advanced dyes

Glamourer's advanced dyes go further than the game's two channels, editing a material's colour rows
directly — diffuse, specular, emissive, gloss and the rest. Wardrobe does not edit those; Glamourer
does, and Wardrobe remembers them.

Off until you turn it on: Settings → **Advanced dyes** → **Keep advanced dyes with outfits**. Nothing
below appears until you do. It stopped being experimental in 1.5.1.1, but stays opt-in — Glamourer has
no API for advanced dyes, so this rides on its state data and is the first thing likely to break when
Glamourer updates. [Settings](Settings.md#advanced-dyes) has the detail.

Under the dye pickers sits an **Advanced dyes** tick box, and next to it two icons:

- **Ticking it** saves the advanced dyes that slot has right now into the outfit. A swatch appears
  for each row saved, in that row's own colour, so you can see at a glance what is kept.
- **Unticking it** forgets them and puts the material back to its game colours.
- The **palette** icon opens Glamourer on your character — the palette icon beside that slot's own
  dyes is the editor.
- The **refresh** icon captures again, replacing what is saved with whatever the slot has on it now.
  Use it after adjusting a colour in Glamourer.

Ticking, capturing again and opening Glamourer all need the piece equipped, because a colour row
describes the material of whatever is in the slot right now rather than a row in an outfit.
Unticking never does — a saved row you could only remove by first equipping the item would be a
trap.

If ticking it finds nothing, the box stays clear and says so, rather than pretending to have saved
something.

Captured rows are applied when the outfit is worn, re-applied after a Penumbra redraw the same way
the plain dyes are, and put back to game values when the piece is unequipped — otherwise they would
go on describing whatever is worn in that slot next.

They are stored per item per outfit, so one piece can be plainly dyed in one outfit and elaborately
dyed in another. Only the rows belonging to that item's slot are captured, so dyeing a hat and
capturing it does not drag the boots' colours in with it.

One caveat worth knowing: the rows are stored exactly as Glamourer wrote them and handed back
untouched, so they are only meaningful against the same piece. Capture again after swapping what is
in that slot. Weapons work, each as its own slot; customisation items have no dyes at all and say
so.

## Vanilla items

An outfit is rarely all mods. Any slot the outfit's own items do not fill is read from Glamourer when
the outfit is saved or updated from what you are wearing, and the plain game item in it — with both
its dyes — is stored alongside. Wearing the outfit puts those back too, so a look made of two mods
and six pieces of ordinary gear saves and restores as the look it actually is.

An outfit of nothing but vanilla items is fine. **Save current look** no longer refuses when the
wardrobe has no part in what you are wearing.

The edit panel lists them under **Vanilla items**, each with its slot, name and dye swatches:

- **Capture what I'm wearing** re-reads Glamourer and replaces what is stored.
- **×** next to a piece stops it being saved with the outfit. It needs Ctrl held, like every other
  one-click delete.

There is no picker for choosing a game item here — that is what Glamourer is for. Wear the look you
want, then capture it.

Slots covered by the outfit's own items are skipped, both when capturing and when wearing. A wardrobe
item carries its mod and options as well as the game item, so it is always the better record; adding
an item to a slot that used to hold a vanilla piece leaves that piece stored but unused rather than
having the two fight over the slot.

## Vanilla glamour plates

The game's own twenty glamour plates can be brought into the wardrobe and worn as outfits, so a look
you already built in-game does not have to be rebuilt here before it can be photographed.

**Sync Glamour Plates**, at the top of the outfits view, reads them and saves each non-empty plate as
an outfit named `Glamour Plate 1`…`20`, tagged with the **Glamour Plate** style so the whole set can
be filtered in or out with the styles dropdown. They sit in the same grid as everything else, with a
blue border and a `Glamour Plate` label on the card.

### You have to open the plate window first

The game only holds your plates in memory after the server has sent them, which happens when you open
the **Glamour Plate** window — at a summoning bell, an inn room, or the Glamour Dresser. Until you
have done that once, there is nothing to read and the sync button says so.

That is a limit on *reading* them. Once a plate is saved it works anywhere, gpose included, with no
bell in sight.

### Two different ways to put one on

There are two, and they are not the same thing. The difference matters, so the wardrobe keeps them
well apart.

**Wear** — on the card, and the ordinary one — equips the plate's pieces through **Glamourer**, which
is a visual override on your own client:

- Works anywhere. No summoning bell, no gear set, and it works in gpose.
- Your real equipment and your real glamour are untouched.
- Only you see it, plus anyone you are synced with through Mare.
- `/glamour disable`, a revert, or logging out puts it back.

**Apply In Game** — the gold button on the card, and again in the edit panel — asks the game to apply
the plate for real:

- Your actual glamour changes. Everyone sees it, and it stays until you change it again.
- It is applied over the gear set you already have equipped, so your **gear and job do not change** —
  only the glamour does.
- One click, no confirmation, exactly as it is from the game's own Gear Set List.
- The game decides whether it is allowed. In combat, in a duty, or somewhere plates are not
  permitted, the button is disabled and says why.

For screenshots, **Wear** is the one you want — it works in gpose and leaves nothing to tidy up.

### Applying in game takes the wardrobe's clothes off

Glamourer sits **on top of** the game. So applying a plate for real while the wardrobe has items on
would change nothing you can see: the plate lands underneath an override that hides it.

So an in-game apply is followed by a revert. The wardrobe takes its own items off — properly, mods
disabled and all, not just cleared out of Glamourer — and then clears Glamourer so the character
shows what the game actually has on. It waits for the game to finish applying before doing this, so
the two do not race.

Animations, VFX and mounts are left running. A plate has nothing to say about them, and a dance
stopping because you changed your shirt would be a surprise.

**Show In-Game Look**, next to the apply button, does the same thing on its own, and the toolbar's
**In-Game Look** button does it from anywhere — useful for answering "what do I actually look like
to everyone else?" without taking every item off by hand. See
[In-Game Look](Wearing-Items.md#in-game-look) for how it sits beside Strip and Unequip All.

### Keeping your character through the revert

A revert clears *everything* the wardrobe was overriding, and that includes the hair, skin, tail and
ears that make the character yours rather than the ones on the character sheet.

**Keep '<name>' on top** — the checkbox beside the button, shown when you have a
[base character](Wearing-Items.md#base-character) set — puts it back afterwards, over the game's
gear. It is on by default, because for most people those are not clothes to be taken off.

All three parts of it come back: the items it names, its design's customisations, and whatever was
worn in the slots it keeps — the tail on a ring that you never added to its item list is put back
too. The plate is the clothes; the base is still the character wearing them.

Turn it off and the revert is absolute: you see the unmodded character the server sees. The setting
applies everywhere the wardrobe reverts, not just to this plate — the same checkbox is in
**Settings → Base character**, where it can be reached without owning a plate at all.

### A weapon your job cannot hold will not glamour

Gear no longer has job restrictions, but weapons still do. Applying a plate whose main hand is a
ninja katana while you are a botanist glamours everything else and leaves the weapon alone, and the
game says so:

> Unable to fully apply glamour plate 1. Some items may be of an incompatible category, or
> contradictory to character race and gender.

That is the game talking, not the wardrobe, and it means what it says — the rest of the plate went
on. The same message covers race- and gender-restricted pieces.

### They are read-only

A plate's contents belong to the game, so the wardrobe shows them and nothing more. There is no
adding, removing, dyeing or **Update from what I'm wearing** on a plate — edit the plate in-game and
resync.

What *is* yours: the **name**, the **preview image** and the **tags**. Rename `Glamour Plate 3` to
`Ballroom`, photograph it, tag it — all of it survives every resync untouched.

**Duplicate As Editable Outfit** is the way out. It copies the plate's pieces into an ordinary outfit
you can edit freely and build wardrobe items on top of, and resyncing the plate will never touch the
copy. It is also the only copy a plate offers — see [Duplicating an outfit](#duplicating-an-outfit).

### Keeping them in step

Plates get edited in-game constantly, so the wardrobe checks its saved copies against the real ones
whenever the game has plate data loaded, and says so when they have drifted:

- **Resync All** brings every changed plate up to date, or **Resync** on a single row for just one.
- **Ignore** hides the notice until the next sync. Nothing is changed.
- A plate whose card carries an amber ● has been changed in-game since it was saved.

If the game has no plate data loaded, nothing is claimed either way — nothing has been compared, and
"up to date" would be a guess rather than a fact. Open the plate window to check.

Plates you empty in-game are reported but **not** deleted. The saved copy is still a wearable look
and may be the only record of it left, so throwing it away because you cleared a plate would destroy
exactly what you cleared the plate to make room for.

### One thing it does not do

Wearing a plate does not clear the slots the plate leaves empty. A plate with no hat, worn over a hat,
keeps the hat. This is not special to plates — no outfit strips vanilla gear it does not have a piece
for — but it shows up more often here, because plates are frequently partial.

## Tags and styles

Outfits carry tags and styles from the same scheme items use — there is no second vocabulary to keep
in step. The edit panel has **Styles** as toggles and **Tags** as chips, with an add box and your
existing tags one click away, exactly as an item's editor does.

The filter row filters outfits too. The **Styles** dropdown beside the search box is there in both
views and means the same thing in each, so ticking **Beach** narrows outfits exactly as it narrows
items — and the filter survives switching between them. Tag filters work the same way, and filtering
on a parent tag still includes everything nested under it.

An outfit card names its styles under the item count, in the first one's colour, and takes that
colour as its tint — so a grid of outfits can be read by eye before any of the names are.

## Editing an outfit by wearing it

Rebuilding a look row by row is guesswork — a swap can only really be judged on the character. So
wear the outfit, change whatever you like by hand, and press **Update** on its card, or **Update from
what I'm wearing** in the edit panel. Everything the wardrobe has on becomes the outfit's contents.

Dyes survive the swap: items still in the outfit keep the ones they had, and a piece moved into a
slot takes over the dye the old occupant had there, on the assumption that the colour belonged to the
outfit rather than to the piece. Re-dye the row afterwards if it did not.

Dyes for items that are no longer worn are dropped, and the outfit's name and preview image are left
alone.

## Why outfits are not Glamourer designs

Outfits store item IDs, not a Glamourer state blob, so wearing one goes through the normal per-item
path — enabling each item's Penumbra mods and applying their options. That is the part a Glamourer
design cannot do on its own.

If an item is deleted later, the card shows how many are missing and the rest still work.
