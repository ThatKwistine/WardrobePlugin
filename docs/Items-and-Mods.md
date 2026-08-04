# Items, Mods and Detection

## What a wardrobe item stores

- **Name** and optional **preview image**
- **Equipment slot** (Head / Body / Hands / Legs / Feet / Ears / Neck / Wrists / Ring / Main Hand / Off Hand)
- **Mods list** — collection name, mod directory, mod name, and saved option group selections for each mod
- **Detected game item** — the FFXIV item row ID and display name auto-detected from the mod's game file paths
- **Tags** — free-form labels for filtering
- **Notes** — free text, searched along with the name; a ¶ on the card means an item has some
- **Date added** — set on import, used for date sorting; hover an item's name to see it
- **Favourite flag** — toggled from the card, filterable from the ♥ button on the slot filter row

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

## Slots contributed by supplementary mods

Slot detection covers the primary mod *and* every supplementary mod, so an upscale that adds legs to
a body-and-hands mod still offers Legs at import. Such slots are marked `· from supplement`. The slot
list rebuilds whenever supplementary mods are added, removed or swapped, preserving anything already
typed or chosen. Where both mods provide a model for the same slot, the primary mod's wins.

## Shared models

Many FFXIV items share one model — the Hakama legs model backs Asuran, Yasha, Yanxian, Nameless and
more. Detection picks the lowest row ID among them, which is arbitrary.

When more than one item shares the detected model, a **Game item** dropdown appears (per slot when
importing, and in the edit panel) listing every candidate so the intended one can be chosen. This
matters beyond cosmetics: the stored item ID is what Glamourer equips and what worn-detection
compares against.

## Re-detecting

If a mod is updated and its file paths change, open the item in the edit panel and click
**Re-detect** to re-run the analysis.

## Variants

**Create variant of this item** in the edit panel copies an item, inheriting its mods, collections,
options, detected game item and image, then opens the copy so you can change what differs.

Because variants occupy the same slot they are never worn at once, so each can hold its own option
selections. Items sharing a mod across *different* slots are worn together, and Penumbra holds only
one option state per mod — so those still share options, and editing one updates the others.

## Option group types

Penumbra groups are `Single`, `Multi`, `Imc`, or `Combining`. Only `Single` is a one-of-N dropdown —
the other three all store a bitmask and are shown as checkboxes.

`Imc` groups are easy to mistake for single-select because their type string isn't "Multi", but each
option carries a power-of-two `AttributeMask` and toggles independently. An unrecognised type is
logged as a warning rather than silently treated as a dropdown.

## Customisation mods

Hair, Face, Tail, Ears (Viera) and Skin replace part of the character model (`chara/human/…`) rather
than an equipped item, so they have no game item, no shared-model picker, and no "Emperor's New" to
strip to. Wearing one just enables its Penumbra mod and applies its options; unwearing disables it.

**Strip** deliberately leaves them alone — it cannot remove a character's hair.

Note this toggles how a hairstyle *looks*; it cannot switch which hairstyle your character has, as
Glamourer's IPC exposes no customisation setter (only `SetItem`, `SetBonusItem` and `SetMetaState`).
Pick the matching hairstyle in the character screen or Glamourer yourself.

### Reverting customisation mods

Set **Settings → Revert customisation mods to** to a Glamourer design holding your character's normal
look. Reverting a hair, face or skin item then re-applies that design's *customisations only* — its
equipment is ignored, so whatever the wardrobe currently has equipped is untouched.

Without a design set, the plugin falls back to restoring just the hairstyle number it noted when the
item was applied.

## Other mod types

**Settings → Manage other mod types** adds Emote, VFX and Mount / Minion to the filter bar and to the
slot pickers. These have no game item to equip, so wearing one only enables its Penumbra mod —
Glamourer is left alone entirely.

It is off by default because the extra filter buttons and slot-picker entries are noise to a wardrobe
made only of gear. Items already imported into those categories are kept but hidden from the grid
while it is off, so turning it back on restores them intact.

Emote mods that replace the same animation swap each other out, like two body mods do. Which
animation an item replaces is detected on import and can be changed when editing it.
