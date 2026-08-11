# Images and Screenshots

## Assigning an image by hand

Open **Images** in the toolbar, point Settings at a folder of images, then drag a thumbnail onto any
item or outfit card.

## Screenshot sessions

A session queues every item that has no preview image, equips each in turn, and picks up new
screenshots from a watched folder automatically.

Options during a session:

- **per-slot GPose camera presets** — saved camera positions per equipment slot, as many as you want
- **strip other items** — persisted between sessions
- **base character** — the customisation mods and slots every shot keeps, chosen from the HUD
- **compact window mode** — keeps the plugin UI out of the shot

Stripping stops at the active [base character](Wearing-Items.md#base-character), which is re-applied
before every shot — so the hair, skin, tail and ear mods that make the character yours are in each
picture rather than stripped out of it.

**Screenshot Outfits** starts a session covering every outfit without a preview, exactly like the
item sessions. [Synced glamour plates](Outfits.md#vanilla-glamour-plates) are outfits like any other,
so they join the run and can be photographed one at a time from their edit panel as well — which is
much of the point of bringing them in.

### A session over just the items you pick

**Select** in the toolbar, tick the items, then **Edit Selected → Screenshots → Photograph**. The
session then covers exactly those items, in place of the whole wardrobe.

This runs **everything you ticked**, including items that already have a preview — those get
replaced. That is the difference between the two: a session over the whole wardrobe photographs what
is *missing*, because that is the only sensible reading of "do the lot". A selection is you saying
which ones, and wanting to redo a preview you are unhappy with is most of the reason to make one.
The panel says how many of the selected already have an image before you start.

The selection survives the session, so a run that goes wrong can be repeated without ticking
everything again.

## Portrait outfit previews

An outfit preview is a full-body shot, and a square crop of one spends most of the frame on the floor
either side of the character. **Portrait outfit previews (9:16)** in Settings → Screenshots matches
GPose's own portrait mode instead: outfit cards grow taller, and the picture keeps the character
rather than the space around them.

It applies to outfits only — item previews are close-ups of one piece and stay square.

Take the shot in GPose's portrait mode and the game saves it upright, ready to crop. A landscape shot
still works — it is centre-cropped to 9:16 like any other picture — but it will only have the middle
column of the frame to give.

Turning it on or off never spoils a wardrobe built under the other setting. Pictures already assigned
are centre-cropped to whichever shape is in use, so a square shot keeps its middle column in a
portrait card and a portrait shot keeps its middle band in a square one. Nothing has to be re-taken,
and no file on disk is touched — only new captures are written in the new shape.

## Previews are never distorted

Screenshot sessions crop the largest centred rectangle of the right shape out of the shot — square,
or 9:16 for an outfit with portrait previews on — so nothing is stretched.

**Captured image size** in Settings → Screenshots chooses how large that is: 512, 1024, 1440 or 2048
pixels — one per common screen height above the default, so 1024 suits 1080p, 1440 suits 1440p and
2048 suits 4K. For a portrait it is the height, and the width follows at 9:16.

512 is enough for a card and keeps a wardrobe of hundreds small on disk. The larger sizes are for
looking at closely — quick view, or a preview on a big screen — and cost roughly four times the space
per step up. It never upscales: the crop can only be as tall as your game window, so a 1080p
screenshot gives about 1080 pixels however large a size is chosen. Existing images are left alone;
the setting applies to shots taken from then on.

Images assigned by hand keep their original file untouched — they are centre-cropped **at draw time**
by adjusting texture coordinates, so a portrait or landscape image fills the square without being
stretched.

## Camera presets

A preset stores the camera angle, tilt, pan, both kinds of zoom, and the field of view, so a session
can frame each slot the same way every time.

Presets belong to the **slot**, not the item — the angle that frames one pair of boots frames them
all.

FFXIV has **two separate zooms** and a preset captures both:

- **Scroll-wheel zoom**, the camera's distance from your character
- **Camera Distance in the Group Pose settings menu**, which despite the name is a field-of-view
  adjustment rather than a distance

Presets are written directly to the game's camera and re-applied for about half a second, because a
single write is overwritten by the game's own per-frame camera update.

They only work with the **native GPose camera**. If BRIO's free camera is active it drives the camera
independently and presets will have no visible effect.

### Several per slot

A slot can hold as many presets as you like, because one angle is rarely enough — a full-body shot
and a close-up are different pictures of the same piece.

Frame the camera in GPose, optionally type a name, and press **Save Camera**. Leaving the name blank
numbers it, so you can catch a good angle without stopping to think of a word for it.

Each preset is a row with three controls:

- **Click the name** to snap the camera to it
- **Update** replaces it with the camera as it is now, keeping the name
- **×** deletes it, while Ctrl is held — see [Deleting things](Items-and-Mods.md#deleting-things)

Right-click the name to **Rename**.

Outfits have their own set, in an outfit's edit panel under **Take Screenshot**. One set for every
outfit rather than one per outfit: the angle that frames one whole look frames the next.

### Angles follow your character

A preset stores its angle relative to the way your character is facing, so a face close-up stays a
face close-up wherever they are stood and whichever way they turned since. GPose cannot turn a
character, so without this the only remedy was to line them up in normal play before entering.

Presets saved before this are labelled **world angle**. They keep pointing the same way whichever way
you face, exactly as they always have — the facing they were saved at was never recorded, so there is
nothing to convert them from. Frame the shot as you want it and press **Update**, and that one starts
following your character. There is no bulk conversion for the same reason: it would have to assume
every preset was saved from one spot, and guessing wrong would break angles that currently work.

### During a session

The compact session window carries the presets for whatever slot is being photographed, so a wrong
angle can be fixed without expanding the window back over the scene you are shooting.

- **Click a preset** to snap to it. The one you last clicked is highlighted, and the one the session
  loads automatically is marked with a `*`. Until you click anything they are the same preset.
- **Update `<name>`** replaces the highlighted preset with the camera as it is now. It names the
  preset it will overwrite, because that changes as you click along the row — click Preset 3, frame
  it, and Update corrects Preset 3.
- **Save New** — or **Save Camera** on a slot with none yet — keeps the current angle as another
  preset.

Renaming, deleting and changing which preset the session loads are left out on purpose: they are
housekeeping, and a stray click on a delete button between shots is a preset gone. **Expand** is
there when you want the full panel.

### Choosing which one loads

Each preset has a radio button beside it. The ticked one is what a **screenshot session applies
automatically**; everything else in the list is there for you to reach by hand.

Ticking one changes nothing about the order, so you can arrange the list however you like and pick
the session default independently.

> Presets saved before pan was supported don't store one, and applying them leaves the camera's pan
> untouched rather than guessing at a centred value. Press **Update** to capture it.

### Coming from a single preset per slot

Slots used to hold exactly one preset. Yours becomes **Preset 1**, ticked as its slot's default, and
that is the whole of it — sessions carry on using the same angle and nothing needs doing.

If one preset per slot is all you want, just never save a second.
