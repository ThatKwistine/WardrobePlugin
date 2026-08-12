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

## Base character

Stripping exists so one piece can be photographed on its own, and by default it takes the character
with it — the tail mod worn on a ring, the ear mod on a pair of earrings, the hair and skin that make
the character yours.

A **base character** is what a strip strips *down to*. Set one up in **Settings → Base character**
and it holds three things:

- **What a strip leaves alone**, in two halves. **Equipment slots**: whatever is worn in a ticked
  slot stays exactly as it is, wardrobe item or plain gear. **Character mods**: the hair, face, tail,
  Viera ear, skin and shared-texture mods you have on stay switched on. A strip cannot *empty* those
  — there is no game item in them — but it does turn their mods off, which takes the character's own
  hair or skin with it, so they get ticks of their own.
- **Items.** Applied with the base and never stripped — hair, skin and tail, plus any gear item that
  is really part of the character. An item's slot is protected on its behalf, so a tail worn on a
  ring needs no separate tick.
- **A Glamourer design.** Only its *customisations* are applied — face, colouring, hairstyle. Its
  gear is ignored, or it would put back the clothes the strip just removed. **Apply in full** beside
  the picker applies the whole design once, by hand, for getting dressed as your character in the
  first place.

Several can be saved and one is active at a time, so a second character — or the same character with
different ears — is a matter of picking one from the list. Switching takes off the items only the old
base wore and applies the new one.

**Apply base character now** puts it on: the design's customisations, then any of its items you are
not already wearing. Nothing is removed.

With no base character active, stripping behaves exactly as it always has.

### In a screenshot session

The base character is applied before **every** shot, not just the first. An item photographed in a
protected slot displaces the base's own piece for that shot, and the base is put back for the next
one — without that, a session drifts further from your character with every item.

The picker is on the session HUD as well as in Settings, because the first shot with your ears
missing is when you go looking for it. It lists what the active base keeps.

Outfit sessions hold the base back too: an outfit shot shows the outfit and nothing else, bar the
character wearing it.

### Strip, Unequip All and In-Game Look

**Strip** keeps the base character. **Unequip All** does not — that is the wardrobe emptying itself
rather than a strip, and it is the way to take a base off without changing any setting.

**In-Game Look** keeps it too, unless you say otherwise — see below.

## In-Game Look

The three take-off buttons in the toolbar do different things:

| Button | What you end up looking like |
| --- | --- |
| **Unequip All** | The wardrobe's items off, base character included. |
| **Strip** | Every equipment slot emptied, stripped down to your base character. |
| **In-Game Look** | Whatever the game actually has on you — your real gear and glamour. |

**In-Game Look** takes the wardrobe's items off *and* clears Glamourer, so nothing is overriding the
character any more. What you see is what everyone else has been seeing all along, including any
[glamour plate](Outfits.md#vanilla-glamour-plates) you have applied.

The items are taken off properly — their Penumbra mods disabled, the wardrobe's record updated —
rather than merely cleared out of Glamourer. A bare revert would leave the mods enabled with nothing
showing them, which is the state the desync notice exists to complain about.

Animations, VFX and mounts are left running, exactly as with **Strip**.

### Keeping your character through it

Clearing every override means clearing the hair, skin, tail and ears that make the character yours
rather than the ones on the character sheet.

**Keep '\<name>' on when showing the in-game look**, in **Settings → Base character**, puts the base
back afterwards over the game's own gear. It is on by default. Turn it off and the revert is
absolute: the unmodded character the server sees.

The same setting appears beside **Show In-Game Look** in a glamour plate's edit panel, because that
is where the question tends to occur to people. It is one setting either way, and it also governs
what happens after applying a plate in game.

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
