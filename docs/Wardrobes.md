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
character's wardrobe is usually that it needs to be different there — another size option, another
collection, another material — so the copy is a template to re-fit, and editing it never touches the
original.

Two directions, same result:

- **Pulling.** **Import → From Another Wardrobe** picks a source, lists its items or its outfits, and
  brings the ticked ones in.
- **Pushing.** Right-click a card's Wear button, or use **Select** and the panel's **Copy to
  wardrobe**, to send from where you are.

What comes with a copy:

- the mods, their options, the game item, the dyes, the notes and the tags
- the pictures, as the same files on disk — the picture is of the piece, and it is the same piece
- **links and variant grouping, within the batch.** Copy an item together with its variants and they
  arrive still grouped; a link to something you left behind is dropped rather than left pointing at
  an item the other wardrobe has never heard of.

What does not:

- **favourites**, which are a judgement about one wardrobe's contents rather than a property of the
  piece
- **an outfit's glamour plate link.** A plate number names a slot in one character's own twenty, so a
  copy claiming plate 4 because the original was plate 4 would be claiming a sync that never happened.

Copying an outfit brings whatever it is made of, reusing anything you have already taken across
rather than making a second copy of it. Anything the target already holds a copy of is skipped, so
using the menu twice does not build a wardrobe of duplicates.

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
