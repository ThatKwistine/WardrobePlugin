# Sharing a wardrobe

A **share file** is your wardrobe, or part of it, in one `.wardrobe` file you can hand to somebody
over Discord. They open it in their own copy of the plugin, browse everything you sent, and take the
pieces they want into their own wardrobe.

Press **Share** on the toolbar.

## What a share file contains

It contains the *description* of your items and outfits:

- the name, slot, tags and notes
- which Penumbra mod each one needs, and which options within that mod
- the game item and model set the wardrobe detected
- for outfits: the pieces, the dyes on each of them, the plain game items filling the other slots,
  and whether the hat and weapon show
- your pictures, if you leave **Include pictures** ticked

## What it does not contain

**Mod files.** Never, in any circumstance.

Redistributing somebody else's mod is not the plugin's to do, and a wardrobe of a few hundred pieces
would be hundreds of megabytes even if it were. A share file says *"this item is the Silk Top mod,
with these options"* — it does not carry the Silk Top mod.

That is the one thing worth understanding before you send one, because it decides what the person on
the other end actually sees.

It also leaves behind a few things that are yours rather than the item's:

| Not shared | Why |
| --- | --- |
| Penumbra collection names | They describe your install. The recipient picks their own on import. |
| Glamourer designs | A design id means nothing in somebody else's Glamourer. |
| Favourites, variants, dates | Your filing, not the item. |
| Base characters | Not yet. |

## Sending one

1. Toolbar → **Share** → **Send a wardrobe**.
2. Optionally put a name in **Shared by** and a line in **Note**. Both are free text, both are
   optional, and nothing is filled in for you.
3. Tick the items to send, and switch to **Outfits** to tick outfits. **Tick All Shown** takes
   everything the search is currently showing in whichever list you are on.
4. **Export…** and choose where to save it.

Sending a lot? Untick **Include pictures**. A wardrobe of three hundred items is a few dozen
kilobytes without them and can be tens of megabytes with them — the items and every mod requirement
still travel either way, so the recipient can still browse and import the lot. They just see
placeholders instead of your photographs.

You can also start from the grid: **Select**, tick some items, then **Share Selected**.

### Outfits bring their pieces with them

Ticking an outfit ticks every item in it, whether or not you chose them yourself — an outfit is a
list of references, and one sent without its pieces arrives empty. Those items show as ticked and
locked in the Items list, and the count beside the tabs says how many came along that way. Untick
the outfit to leave them out.

### Plates and design cards change when shared

A **glamour plate outfit** is sent as an ordinary outfit holding the same gear. It cannot stay
attached to a plate, because plate 3 on somebody else's account holds their glamour, not yours. The
gear itself travels perfectly, so this is usually exactly what you wanted.

A **Glamourer design card** is sent as an ordinary outfit holding the items attached to it. The
design does not travel — its id means nothing in anybody else's Glamourer — so any gear or colouring
that lived in the design is not in the file. If a card is mostly its design rather than its attached
items, very little of it will arrive, and the send list marks it so you know before you send it.

## Opening one somebody sent you

Toolbar → **Share** → **Open one I was sent** → **Open a share file…**

Every item appears as a card. What you can do with each depends on whether you already own the mod
behind it:

- **Normal card, with Add and Add and Wear** — you have the mod. Adding brings the item into your
  wardrobe as an ordinary item, filed under the collection chosen in the bar at the top.
- **Greyed card, reading "Needs …"** — you do not have that mod. The card still shows you what the
  item is, and if the sender wrote a link into the item's notes it appears under the card so you can
  go and find it. Nothing else can be done from here; install the mod, then reopen the file.
- **"Missing 1 extra"** — you have the mod that *is* the item, but not an upscale or compatibility
  patch it was sent with. It will go on and look very nearly right.

**Only what I can wear** hides the greyed ones once you have had a look.

### Outfits you were sent

Switch to **Outfits** to see them. An outfit card says how much of it you could put together:

- **No badge** — you have every piece. It will arrive exactly as its sender has it.
- **"3 of 5 pieces"** — you have some of the modded pieces. Adding it gives you the outfit with the
  other two missing; find them in the Items list, greyed, to see what to install.
- **"No pieces you have"** — none of it can be assembled, and there is nothing to add.

Plain game items in an outfit always work, so a shared glamour plate — or any mostly-unmodded look —
usually arrives intact whatever mods you own.

**Add** on an outfit brings in the outfit *and* any of its pieces you do not already have, so you do
not have to collect them from the Items list first.

### Why Add and Wear adds first

There is no "try it on without keeping it". Wearing an item records it as worn, and everything that
later takes it off — unequip, Strip, the bookkeeping that knows which mods the wardrobe turned on —
finds it by looking it up in your wardrobe. An item worn without being in there would go on and never
come off, leaving its mods enabled with nothing recording why.

So it is added, then worn, and you can delete it afterwards like any other item. Leave **Tag what I
add with the sender's name** ticked and everything from one file lands under one tag, which makes
them easy to find again — and easy to remove together if you change your mind.

### Importing the same file twice

Items you have already taken show **In your wardrobe** rather than offering to add a second copy.
That is matched on the item's identity inside the bundle, so re-opening a file after installing a
few more mods is a normal thing to do — you will only be offered the ones you did not take.

## Is it safe to open a file from someone I don't know?

The file is read as untrusted input: only pictures are unpacked from it, oversized entries are
refused, and entry names are rewritten rather than trusted, so nothing in an archive can write
outside the folder it is given. It cannot install a mod, because it contains none.

The real caution is duller: a share file can name any mod at all, and a card that says
*"Needs Something Nice"* is the sender's text, not a verified fact about a real mod. Links in notes
are the sender's too. Treat them as you would any link somebody sent you.
