# Custom Slot Icons and Icon Packs

Every slot in the wardrobe — the buttons along the filter bar, the badge on each item card — can be
drawn with your own artwork instead of the built-in game icons or Font Awesome glyphs.

There is no icon format to learn. A set of icons is a folder of ordinary images, each named after the
slot it replaces, and a **pack** is that folder zipped up so it can be handed to someone else.

Settings → **Slot Icons** is where all of this lives. It only appears once **Use icons for slots** is
on.

---

## Installing a pack someone gave you

1. Settings → **Slot Icons** → **Icon pack** → **Import zip…**
2. Pick the `.zip`. It is only read — the file stays wherever you keep it.
3. The pack is unpacked into the plugin's own folder and selected straight away.

The dropdown lists every installed pack. Choosing **None** goes back to the built-in set without
uninstalling anything, so switching between two packs costs one click each way.

Each pack in the dropdown has an **x** beside it. That removes the pack: its folder and the images in
it are deleted after a confirmation. Your own icon folder, your items and your images are untouched,
and re-importing the zip brings it back.

**Copy path** puts the packs folder on the clipboard, for when you want to open one in Explorer and
edit it, or zip it back up to pass on.

A pack that covers no slots is still installed, but nothing will change on screen. That almost always
means the file names do not match — see the list below, and open **Which slots** to see what each
slot is looking for.

---

## Making your own icons

### The images

- `.png`, `.webp`, `.jpg`, `.jpeg`, `.bmp` and `.tga` are all accepted. If a slot has more than one,
  `.png` wins, then the rest in that order.
- They need not be square. Images are centre-cropped to a square, not squashed.
- Roughly 64×64 to 256×256 is ample. Icons are drawn at about 20 px by default and scale with the
  sliders and Dalamud's Global Font Scale, so anything larger is only paying for itself if you scale
  icons up a long way.
- Transparency works and is usually what you want, since icons sit on the window background.

### The names

The file name is the whole mechanism. Case does not matter — `head.png` is as good as `Head.png` —
but spelling does, and a misspelt file is invisible: it simply never appears, with nothing to say
why.

| Slot | Name the file | Also accepted |
| --- | --- | --- |
| Head | `Head` | |
| Body | `Body` | |
| Hands | `Hands` | |
| Legs | `Legs` | |
| Feet | `Feet` | |
| Earrings | `Ears` | `Earrings`, `Earring` |
| Neck | `Neck` | |
| Wrists | `Wrists` | |
| Ring (R) | `RingRight` | `RightRing`, `RingR` |
| Ring (L) | `RingLeft` | `LeftRing`, `RingL` |
| Main Hand | `MainHand` | `Weapon` |
| Off Hand | `OffHand` | `Shield` |
| Hair | `Hair` | |
| Face | `Face` | |
| Tail | `Tail` | |
| Viera Ears | `VieraEars` | |
| Skin | `Skin` | |
| Animation | `Animation` | |
| VFX | `Vfx` | `VFX` |
| Mount / Minion | `Mount` | `Minion`, `MountMinion` |

The last three only appear if **Manage mods that are not equipment** is on.

`Ears` is the earring accessory slot; Viera ear models are `VieraEars`. Nothing is required — supply
two files and two icons change, and every other slot stays on the set chosen in **Icon set**.

In the plugin, **Which slots** under the icon settings lists what was matched and, for anything
missing, the file name it wants. Hovering a slot there shows every name it will answer to.

---

## Your own folder

**Your own folder** points at any folder on your machine and works exactly the same way — the same
names, the same rules.

It is layered *over* the pack. A file there replaces that one slot and leaves the rest of the pack
alone, which is the point: you can install someone's full set and still override the two icons you
did not get on with, without unpacking or editing theirs.

**Rescan** re-reads both, for after adding or renaming a file while the game is running. **Clear**
drops the folder and leaves any pack in place.

---

## Building a pack to share

1. Make a folder and fill it with your images, named as above.
2. Optionally add a `pack.json` beside them:

   ```json
   {
     "Name": "Moonlit Icons",
     "Author": "Your Name"
   }
   ```

   Without it the pack is named after the zip file.
3. Zip the folder. Zipping the folder itself is fine — images are pulled out of whatever
   subdirectories they sit in, so both a zip of loose files and a zip of one folder install the same
   way.

Only image files are unpacked; anything else in the zip is ignored. Nested folders are flattened, so
two files with the same name in different folders will collide and the first wins — keep one image
per slot.

Once installed, a pack is just a folder again. **Copy path**, open it in Explorer, and it can be
edited or re-zipped like any other.

---

## When an icon does not appear

- **Check the name first.** Open **Which slots** — a slot listed as missing is a naming problem nine
  times out of ten. Watch for hidden double extensions such as `Head.png.png`.
- **Press Rescan** if the file was added while the game was running.
- **Check the format.** A `.png` that is really something else renamed will not load.
- **Check nothing is layered over it.** Your own folder beats the pack for that slot.
- If a whole pack vanished, look at the dropdown: a pack removed from disk outside the game shows as
  *no longer installed*, and the slots fall back to the built-in set.
