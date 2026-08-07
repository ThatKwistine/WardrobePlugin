# Tags

Tags are free-form labels for finding things. An item can carry as many as you like, and the
**Tags** panel in the toolbar filters the grid by them.

## Sub-tags

A `/` makes a tag nested: `Shoes/Boots/Ankle Boots` is one tag, drawn in the panel as three levels
you can expand and collapse. Filtering on a parent shows everything beneath it, so ticking `Shoes`
covers every kind of boot without naming them.

Spacing around the slashes does not matter — `Shoes / Boots` and `Shoes/Boots` are the same tag.

## Making tags ahead of use

Tags normally come into existence by being typed onto an item, which means the list can only be
built one item at a time and a typo silently becomes a second tag rather than an error.

The **new tag** box at the top of the Tags panel makes one before anything has it. Type a name —
`/` for sub-tags, same as anywhere else — and press **Make Tag**. This is worth doing when you know
the scheme you want: lay the whole thing out first, then apply it.

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
- **To a batch.** **Select** in the toolbar, tick the items, then **Edit Selected → Tags**. **Add
  Tag** and **Remove Tag** act on the whole selection, leaving alone both the tags an item already
  has and any tag you are not naming. This is the fastest way to use a tag you made ahead of time.

  The panel shows the tag tree inline, in the same nested shape as the Tags panel — click a tag to
  put it in the box, or type a new one. Branches collapse, so this stays usable as the tag list
  grows, and seeing what already exists under a heading is what stops a near-duplicate being typed
  in beside it. Greyed tags are ones no item has yet.

## Filtering

Click a tag in the panel to filter the grid to it; click again to unfilter. Several tags can be
active at once, and **× Clear** drops them all. The header shows how many are active.

Tag filters combine with the search box and the slot, worn and favourite filters rather than
replacing them — narrow the grid however you like, and **Select All** in select mode then acts on
exactly what is showing.

The search box also matches tag text, so typing a tag name finds its items without touching the
panel.
