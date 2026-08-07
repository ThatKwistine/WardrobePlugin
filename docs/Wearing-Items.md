# Wearing and Unequipping

## Wearing an item

Click **Wear** on any card, or use `/wardrobe wear <item name>`.

Multiple items can be worn at once as long as they occupy different slots. Two items sharing the
same underlying mod are handled correctly — the mod stays enabled until all items using it are
unequipped.

## What actually happens

1. If something is already worn in that slot it is unequipped first
2. Glamourer's `SetItem.V3` is called *before* any mod is touched
3. For each mod: the mod is **enabled first**, then its option groups are applied
4. `SetItem.V3` is called again, in case step 1 overwrote the slot
5. If any mod was newly enabled, the item is re-applied a few times over the next several seconds

## Why the order matters

Enabling a mod makes Penumbra start an async resource reload, and Glamourer snapshots its state when
that reload begins, re-applying the snapshot when it finishes. Calling `SetItem` first puts the right
item in that snapshot.

Enabling the mod *before* applying its options matters for the same reason — options set on a
disabled mod are not picked up by the initial enable-reload, whereas changing an option on an
already-enabled mod triggers a fresh reload that reads the correct state.

The delayed re-applies (350 ms / 750 ms / 1800 ms / 4500 ms) exist because a mod with missing
material files can keep reloading for several seconds, and each reload can undo the Glamourer state.

## Unequipping

Click **Unequip** on a worn card (shown with a filled circle), or use `/wardrobe unequip`.

1. The Penumbra mod is disabled (unless another worn item still needs it)
2. Glamourer's `SetItem.V3` is called with the Emperor's New item for that slot, making it appear empty

## Linked items

Two items can be linked so they are worn and taken off together — a top and the gloves that finish
it, a hair mod and the accessory that sits in it.

A linked item's card says so in its button: **Wear +1**, **Unequip +2**. Hovering it lists exactly
what comes with it. Underneath is **Wear only this** (or **Unequip only this**), which acts on that
one item and leaves its partners alone. The same action is on the right-click menu of the main
button.

Wearing a linked item skips any partner already worn, so nothing is needlessly re-applied.
Unequipping only takes off the partners that are actually on.

### Linking items

Two ways in, and they do the same thing:

- **Select** in the toolbar, tick two or more items, then **Edit Selected → Link**. This links every
  item in the selection to every other one.
- The **Linked items** section in an item's edit panel, which lists its partners and has a search
  box to add another.

Both write the link to *both* items, so it can be broken from either side. Because of that, changes
in the edit panel apply immediately rather than waiting for **Save** — there is no sensible way for
**Cancel** to take back half a link the other item already knows about.

### What links do not do

- **They do not chain.** If A is linked to B and B to C, wearing A brings B and stops there. What an
  item pulls in is exactly the list shown on its card and in its editor, never anything further out.
  To make a set of three all bring each other, link all three (selecting all three and pressing
  **Link** does this).
- **They do not cross a slot.** Two items in the same slot cannot be linked: wearing one takes the
  other off, so the link would come apart every time it was used. The **Link** button reports how
  many pairs it refused for this reason, and the edit panel's picker lists them greyed out with the
  reason rather than hiding them.
- **They do not affect outfits.** An outfit is an explicit list of items, so wearing one wears
  exactly what it holds. Screenshot sessions and the desync notice likewise act on single items.

### Unlinking

- **Edit Selected → Unlink** breaks links *between* the selected items.
- **Clear every link on these items** below it breaks all their links, including to items that are
  not selected.
- The **×** beside a partner in the edit panel breaks that one link.

Deleting an item removes it from its partners' links.

## Worn-item detection

The plugin works out what is already worn by cross-referencing Penumbra's enabled mods and option
selections against Glamourer's equipped items.

An item only registers as worn if its mods are enabled **and** their option selections — single- and
multi-select alike — match what the item saved. Two items sharing a mod but wanting different
checkboxes will not both register.

## Desync recovery

Sometimes a mod ends up enabled in Penumbra without Glamourer showing it — usually after a crash or
a manual change in Penumbra. The wardrobe flags these items, because **Unequip All** would otherwise
have nothing to unequip while the mod stays on.
