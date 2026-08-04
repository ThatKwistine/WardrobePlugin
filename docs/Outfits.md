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

Outfit cards are drawn larger than item cards by default, since outfit previews are usually
full-body shots rather than close-ups. **Large cards** turns that off to match the item grid.

## The edit panel

Every item in the outfit is listed as a row with a small thumbnail, its name and slot, and:

- an **Equip / Unequip** toggle so pieces can be tried individually
- a **+ Dye** button that equips the piece with its configured dyes without wearing the rest
- **Remove from outfit**

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
