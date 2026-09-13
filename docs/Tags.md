# Tags

Tags are free-form labels for finding things. An item can carry as many as you like, and the
**Tags** panel, under **View**, filters the grid by them.

## Styles

A **style** is the mood or theme of a piece rather than what it is — Casual, Beach, Comfy, Formal.
Where a tag says a thing is a pair of boots, a style says what it is *for*, which is the question
you are actually asking when a hundred chest pieces are in front of you.

Styles get their own dropdown beside the search box, so narrowing to everything comfy is one click
and never involves opening a panel. It appears once you have at least one style, and stays out of the
way until then.

Underneath they are ordinary tags filed under `Style/` — `Style/Casual`, `Style/Beach` — so
everything tags can do they can do too: made ahead of use, applied to a whole selection at once,
carried in backups, and found by the search box. What makes them styles is only where they are
shown. You never have to type the `Style/` part; the plugin adds it.

### Making styles

The **Styles** section at the top of the Tags panel has a **new style** box. Type a name and press
**Make Style**. Each of the panel's two sections works the same way: the box that makes a thing sits
at the top of the section that lists them.

If you have no styles at all, that section also offers a ready-made set — Casual, Comfy, Cute,
Elegant, Formal, Beach, Sleepy, Tech, Fantasy and Modern — behind an **Add these** button. It is
offered once. Taking it or pressing **No thanks** both settle the question for good, so a scheme of
your own never has to be built on top of ten guesses. Anything you take and then dislike can be
deleted.

Styles are listed as buttons in that section: click one to filter by it, and right-click a greyed
one — a style no item has yet — to delete it. As with tags, a style something is wearing cannot be
deleted out from under it.

### Applying styles

- **In the edit panel.** The **Styles** row above the tags is a set of toggles, one per style. Click
  to put one on, click again to take it off. Toggles rather than typing, because a style is only
  worth anything if everything in it is spelled the same way.
- **To a batch.** **Select → Edit Selected → Tags** shows styles in the tree under a `Style` branch.
  This is the fastest way to style a wardrobe that already exists.
- **While importing.** Both import panels show them in the same place, under `Style` in the tag
  tree, so a batch that hangs together can be styled as it arrives.

## Renaming

Right-click any tag or style and choose **Rename**. A box takes its place in the list; type the new
name and press Enter or **Save**. It works on tags in use as well as unused ones — fixing a typo no
longer means re-tagging everything by hand.

The rename reaches everywhere the tag exists at once: every item carrying it, the pre-made list, any
filter currently on, and its colour. Renaming a parent takes its sub-tags with it, so renaming
`Shoes` to `Footwear` turns `Shoes/Boots` into `Footwear/Boots`.

Only the last part of the path changes, so a style stays a style and a sub-tag keeps its parent. A
name already taken is refused rather than merged into the existing tag — merging two tags is a
bigger thing than fixing a typo, and it cannot be undone. Rename to a free name instead.

## Colours

Right-click any tag or style in the Tags panel to open a colour picker, with the usual 0–255 R, G
and B boxes beside it. The colour applies as you drag, and **Reset colour** puts it back to the
default. It works on tags in use as well as unused ones — deleting is still limited to tags nothing
carries, but colouring is not.

A colour survives every state the tag can be in. The hue stays put and only the brightness moves, so
a green tag looks green whether it is filtering (brighter), idle, or has nothing carrying it yet
(dimmer). Colour says what the tag is; brightness says what it is doing.

**An item card takes the colour of its style.** Colour a style Beach and everything in it tints the
same way, which sorts a grid by mood at a glance before you read a single name. It is styles rather
than tags because a style describes a whole piece, and an item wears at most a couple — a card tinted
by whichever of eight tags happened to sort first would be colour without meaning. If an item does
carry two coloured styles, the first alphabetically wins, so the grid does not reshuffle its colours
when a tag list is reordered.

A **worn** item keeps its gold card whatever its style: that has to stay unmistakable, and two
things competing to colour one card says neither clearly.

All of this is behind Settings → **Tag Colours** → **Colour tags and styles**, on by default —
nothing changes until you pick a colour. Turning it off keeps every colour you have chosen and only
stops them being used, so it is a switch rather than a purge.

## Folders

A **folder** is where a card is filed, as distinct from what it is or what mood it suits. Once a
folder exists, a **Folders** button appears on the filter row beside Worn and Variants, and it works
like them: on, the grid shows its folders — one card per folder, a 3x3 of what is inside, the name,
and the count; off, every card stands on its own, filed or not. Click a folder card, or its **Open**
button, to see the contents; **Back** and the crumbs above the grid lead out again. Folders nest:
`Animations/Idles` is a folder inside a folder, and opening `Animations` shows `Idles` as a card
among whatever is filed directly in `Animations`. Turning the filter off leaves whatever folder was
open, so the next press starts at the top.

Both grids have them, and they share one set: an outfit filed in `Summer` is in the same folder as
the items filed there, though each grid keeps its own place — opening a folder on the items grid
does not move the outfits grid. A folder card on the items grid counts items; on the outfits grid,
outfits.

Filing is a move. **Select** some cards and use **Add to Folder…** on the bar above the grid (or
**Put in folder…** in the Edit Selected panel), or right-click a card (an outfit's Edit button) for
the same menu, and pick a folder or type a new one — a slash nests it. A card leaves whatever folder
it was in, and an item's variants go with it. **Take out** puts things back at the top. Filing
changes nothing on the grid until the Folders filter is on; the card stays where it was.

The other way round works too: inside an open folder, **Add to this folder…** on the crumb row opens
a picker over the whole wardrobe — search, tick, add — listing everything not already filed there,
and saying where each ticked card is filed now. Above the cards it lists the other folders, and a
ticked folder moves inside this one with everything in it — the only way to move a folder, since
renaming changes only its last segment.

**View → Folders** opens a panel listing every folder as a tree with how much is filed under each,
where folders can be made ahead of use — type a name, **Make** — and clicked to open on the grid, or
right-clicked to rename, colour or delete.

With **Folders From Penumbra** turned on (Settings → Grid & Cards, marked Experimental), the panel
also has **File items by Penumbra folder**: it reads where each item's mod sits on Penumbra's own mod
list and files the item under the same folder path, so a mod filed as `Gear/Dresses/Red Dress` puts
its item in `Gear/Dresses`. It reads the first mod on each item, the one it was imported from. Mods
at Penumbra's root, or never filed there, are left alone, and nothing in Penumbra is changed. By
default an item already in a folder is kept where it is; untick **Only items not already in a
folder** to let the mod list win.

The filter composes with the rest. Search and the other filters look inside every folder, and a
folder's name counts as part of what is inside it — search "summer" and everything filed under
`Summer` matches, so the folder card shows with its full count. Search for "idle" with Folders on
and the grid shows the folders that hold a match, each saying how much of it matched — three folders
rather than a hundred cards — and a folder with nothing matching is not shown. An empty folder is
shown when nothing else is being filtered, so a folder you have just made or just emptied does not
vanish.

Right-click a folder card to **rename** it, give it a **colour** (the card takes the tint, as a
style's does), **take everything out**, or **delete** it — which unfiles what is inside and forgets
the folder. Nothing in a folder is ever deleted with it.

Underneath, a folder is an ordinary tag filed under `Folder/` — `Folder/Animations/Idles` — the same
way a style is a tag under `Style/`. That is what makes nesting, renaming, colours, bulk filing,
search, backups and sharing work on folders for free. It also means an item *can* be in two folders
if you tag it by hand, and that the order inside a folder is the grid's sort. The folder branch is
kept out of the tag tree, since the Folders button is its control.

## Sub-tags

A `/` makes a tag nested: `Shoes/Boots/Ankle Boots` is one tag, drawn in the panel as three levels
you can expand and collapse. Filtering on a parent shows everything beneath it, so ticking `Shoes`
covers every kind of boot without naming them.

Spacing around the slashes does not matter — `Shoes / Boots` and `Shoes/Boots` are the same tag.

## Tags from Glamourer folders

If you show your Glamourer designs in the outfits grid, Wardrobe can tag each design card from where
that design is filed in Glamourer.

Glamourer's folder tree and sub-tags nest the same way, on `/`, so a path is already a tag. A design
filed under `Night/Formal` gets exactly the tag `Night/Formal` — it appears under `Night` in the panel
beside everything else there, and filtering on `Night` covers the lot. Designs sitting at the top
level in Glamourer have no folder, so they get no tag.

Designs can also carry tags of their own in Glamourer. Those come across too, flat rather than
nested, since they say what a design *is* rather than where it is filed. A slash inside one becomes
a `-`, so a Glamourer tag never quietly creates a branch. Turn this half off under
**Settings → Tags From Glamourer** if you only want the folders.

### It only ever adds

Nothing here removes a tag, ever. Tags on a card are yours — whether you typed them or an earlier
import put them there — and a folder you have since renamed is not grounds for taking one off.

The trade-off is deliberate: reorganise your designs in Glamourer, run the import again, and you get
the new tags *alongside* the old ones rather than instead of them. Tidying those up is a job for the
Tags panel, and that is the recoverable direction. The alternative loses work.

### Turning it on

- **A wardrobe with design cards already in it** is asked once, with a notice above the grid. Taking
  the offer tags what you have and turns it on for cards that appear later; **No Thanks** settles it.
- **A wardrobe with no design cards yet** — including one that has never switched designs on — just
  has it on. There is nothing to disturb, so nothing is asked.

Either way, **Settings → Tags From Glamourer** has the switch and an **Import tags** button that runs
over every card you have. That button is safe to press repeatedly: it adds only what is missing, so a
second run changes nothing.

> One curiosity: a Glamourer folder named `Style` produces wardrobe *styles*, since that is how
> styles are stored. Harmless, and occasionally what you wanted.

## Making tags ahead of use

Tags normally come into existence by being typed onto an item, which means the list can only be
built one item at a time and a typo silently becomes a second tag rather than an error.

The **new tag** box at the top of the panel's **Tags** section — directly above the tree it fills —
makes one before anything has it. Type a name, `/` for sub-tags as anywhere else, and press **Make
Tag**. This is worth doing when you know the scheme you want: lay the whole thing out first, then
apply it.

A tag with no items yet is shown **greyed out** in the tree. It behaves like any other tag — it
appears in every tag picker, ready to be applied — but filtering on it shows nothing, because
nothing has it. Once an item takes the tag, it stops being greyed.

To delete one, right-click it and choose **Delete**. This only works on greyed-out tags, so deleting
can never take a tag off an item. Deleting a parent deletes any unused tags nested under it.

## Applying tags

- **While importing.** The import panel has a **Tags & Notes** section above the **Import** button.
  Tags added there go onto everything that import creates, and the notes are written on all of them.
  It shows the same tag tree as everywhere else, so a scheme laid out in the Tags panel is one click
  away rather than something to retype.

  One set for the whole import, not one per slot: a mod covering body and legs makes two items that
  are the same outfit, creator and body type, so per-slot boxes would mean typing it twice. Anything
  that genuinely differs can be changed in the edit panel afterwards.

  This is the moment the information is actually to hand. Left until later, tagging tends not to
  happen at all.
- **While mass importing.** The **Tags** button beside **Import Mods** does the same for a batch. It
  carries a count once anything is set, so tags chosen and then dismissed are still visible before
  you import.

  One set for the whole batch, not one per row: a tag control on each of a few hundred rows would
  bury the list. That suits the tags worth setting at this point, which are the ones that are not
  about any individual piece — a body type, a creator, where the batch came from. Anything belonging
  to one item is better applied afterwards by selecting it in the grid.
- **In the edit panel.** Type a tag and press **Add**, or click one of the suggestions listed
  underneath — every known tag appears there, including ones made ahead of use. Right-click a
  suggestion to edit it before adding, which is how you make a sibling of an existing sub-tag
  without retyping the whole path.
- **To a batch.** **View → Select Mode**, tick the items, then **Edit Selected → Tags**. **Add
  Tag** and **Remove Tag** act on the whole selection, leaving alone both the tags an item already
  has and any tag you are not naming. This is the fastest way to use a tag you made ahead of time.

  The panel shows the tag tree inline, in the same nested shape as the Tags panel — click a tag to
  put it in the box, or type a new one. Branches collapse, so this stays usable as the tag list
  grows, and seeing what already exists under a heading is what stops a near-duplicate being typed
  in beside it. Greyed tags are ones no item has yet.

- **While mass importing.** Two levels, and an item gets both. **Batch Tags**, on the Import row, go
  on everything the import creates — best for what is true of the whole batch, a body type or a
  creator. The **Tags** column on each row covers that mod alone, and every item that mod produces
  gets them.

  A row carrying tags is coloured, so a long list can be scanned for what has been done. Tagging as
  you go is much less work than finding the same pieces again afterwards, since the import screen is
  the one place you can still see which mod each item came from.

## Filtering

Click a tag in the panel to filter the grid to it; click again to unfilter. Several tags can be
active at once, and **× Clear** drops them all. The header shows how many are active.

Tag filters combine with the search box and the slot, worn and favourite filters rather than
replacing them — narrow the grid however you like, and **Select All** in select mode then acts on
exactly what is showing.

Styles combine the other way round from each other than they do with tags. Two styles widen the
grid, as two tags do: Casual and Beach shows everything in either. But a style and a tag narrow it —
Casual plus `Shoes` gives you casual shoes, not everything that is one or the other. That is the
question the row is there to answer.

The search box also matches tag text, so typing a tag name finds its items without touching the
panel.
