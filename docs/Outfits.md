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

Outfit cards are the same size as item cards. The magnifying glass on the toolbar scales both grids
together — worth turning up if your outfit previews are full-body shots showing too little. See
[Card size](Settings.md#card-size).

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

This is **experimental** and off until you turn it on: Settings → **Experimental** → **Keep advanced
dyes with outfits**. Nothing below appears until you do. Glamourer has no API for advanced dyes, so
this rides on its state data and is the first thing likely to break when Glamourer updates —
[Settings](Settings.md#advanced-dyes) has the detail.

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
