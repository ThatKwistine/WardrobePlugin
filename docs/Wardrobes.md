# A wardrobe per character

Paths below are relative to the repository root, not to this folder.

Off unless you ask for it. With it off there is one wardrobe, it belongs to nobody, and nothing ever
switches — which is exactly how the plugin worked before this existed, and what your current wardrobe
still is.

Turn it on in **Settings → Characters → Per-Character Wardrobes**, or on the third step of first-time
setup.

## What is separate, and what is not

Each wardrobe holds one character's:

- items and outfits
- base characters, and which one is active
- camera angles, and the file they export to
- pictures folder

Everything else is shared across all of them:

- **tags and styles** — a scheme is built once and means the same thing on every character, which is
  also what keeps a copied item's tags meaningful
- the FFXIV screenshots folder, captured image size and crop guide
- backups, icon packs, and every other setting

Tags being shared is deliberate rather than an oversight. A taxonomy that meant something different
per character would have to be rebuilt from nothing for every alt, and `Formal` on one character
would tell you nothing about `Formal` on another.

## Which wardrobe loads

Logging in looks for a wardrobe bound to that character and switches to it. Nothing else switches
anything: the wardrobe in force is what **Strip** and **Unequip All** act on, so it is never changed
under you without a click.

A character the plugin has not seen before gets a notice above the grid offering three answers:

| Answer | What happens |
|---|---|
| **Use '<name>'** | Binds them to the wardrobe you are already on. For a second character who wears the same things. |
| **New wardrobe for <name>** | Starts an empty one and switches to it. Tags and styles come with it; items do not. |
| **Not Now** | Nothing changes. You are asked again the next time you log in as them. |

Until you answer, you stay on whichever wardrobe you were already using.

## Binding by hand

**Settings → Characters → Per-Character Wardrobes** lists every wardrobe with the characters it
loads for, and a button that binds or unbinds whoever is logged in right now.

Only the character actually on screen can be bound. A binding has to match what the game reports
exactly, down to the home world's id, and a name typed one character out would fail silently at the
only moment it matters — the next login. So there is no box to type a name into.

A wardrobe can hold **several** characters, and that is how two of them share one: bind both, and the
button reads **Share with** rather than **Bind** once somebody is already on the list. The other
direction stays exclusive — binding a character removes them from any other wardrobe, because a
character on two wardrobes would load whichever happened to sit earlier in the list.

## Copying between wardrobes

Pieces move as **copies**, never as shared references. The reason to put something in a second
character's wardrobe is usually that it needs to be different there — another collection, another
material — so the copy is a template to re-fit, and editing it never touches the original. A body
size is the quickest of those edits: the copy keeps its size group, so it is a pick on the card
rather than a trip through Edit. See [Sizes](Wearing-Items.md#sizes).

Two directions, same result:

- **Pulling.** **Import → From Another Wardrobe** picks a source, lists its items or its outfits, and
  brings the ticked ones in. The item list can be narrowed to a slot, ordered by slot, by name or
  newest first, and searched by name, tag, note, game item or mod. Each row carries a small picture
  (hover it for a bigger one) unless **Pictures** is switched off. Variants sit folded under their
  original, and ticking an original ticks its variants and its linked pieces too — untick any you do
  not want. **Tag with '…'** puts the source wardrobe's name on everything that arrives, so the copies
  can be found again once they are mixed in with the rest; it is off unless you turn it on.
- **Pushing.** Right-click a card and choose **Copy to wardrobe**, or use **Select** and the
  panel's **Copy to wardrobe**, to send from where you are. What you send takes its variants and linked pieces along,
  and the menu says how many that adds.

What comes with a copy:

- the mods, their options and size groups, the game item, the dyes, the notes and the tags
- the pictures, as the same files on disk — the picture is of the piece, and it is the same piece.
  Tick **Copy pictures too** (in the Import panel, or at the top of the Copy to wardrobe menu) and
  the files are copied into the other wardrobe's own pictures folder instead, so the copy has
  pictures of its own to re-shoot or tidy without touching the original's. One setting for both
  directions, off unless you turn it on, and it does nothing for a wardrobe with no folder of its own.
- **links and variant grouping.** They are pointed at the other wardrobe's copies — the ones made in
  the same batch, or the ones already there from an earlier one, so a variant brought over a week
  after its original still folds under it. A link to something the other wardrobe has never seen is
  dropped rather than left pointing at an item it cannot find.

What does not:

- **favourites**, which are a judgement about one wardrobe's contents rather than a property of the
  piece
- **an outfit's glamour plate link.** A plate number names a slot in one character's own twenty, so a
  copy claiming plate 4 because the original was plate 4 would be claiming a sync that never happened.

Copying an outfit brings whatever it is made of, reusing anything already there rather than making a
second copy of it; the outfit's row says how many of its pieces that is, and hovering it lists them.

Anything the other wardrobe already has is greyed out and skipped, so using the menu twice does not
build a wardrobe of duplicates. "Already has" is read four ways: it holds a copy of the piece, it
holds the original the piece was copied from, both are copies of the same original, or it holds the
**same mod at the same options in the same slot** — which is what two wardrobes made from the same
Penumbra install end up with when each character imported the mod on their own. The row's tooltip
says which of those it found and which item it matched. Names are never compared: two characters can
each have a "Summer dress" that are different dresses, and a name is the first thing anybody changes
on a copy.

## Things worth knowing

- **A new wardrobe starts with no camera presets file.** Angles live in the config either way; the
  file is an export and a backup. One shared file across several wardrobes would have each of them
  overwrite the last, so a new wardrobe is given none rather than inheriting one.
- **The pictures folder is carried over** when a wardrobe is made, so a new one can save a screenshot
  from the start. Change it per character if you would rather keep them apart.
- **Deleting a wardrobe deletes what is in it** — its items and outfits, not the mods or the pictures
  on disk. The last one cannot be deleted; everything reads through a wardrobe, so a config with none
  has nowhere to put an item.
- **Backups cover every wardrobe**, since they copy the whole config file.
- **The web page export and share files describe the wardrobe you are on**, not all of them.
