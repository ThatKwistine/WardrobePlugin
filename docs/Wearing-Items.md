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
