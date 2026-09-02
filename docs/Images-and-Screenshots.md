# Images and Screenshots

## Assigning an image by hand

Open **View → Images**, point Settings at a folder of images, then drag a thumbnail onto any
item or outfit card.

## Several pictures per item

An item or outfit can hold as many pictures as you like. The first is the **cover** — the one on the
card, in tooltips and everywhere else a single picture is shown — and the rest sit behind it.

The **Pictures** row in the edit panel is where they are managed, on items and outfits alike:

- **Drag a picture from the Image Browser** onto the row's **+** box to add it. Dropping one on a card
  still sets the cover, as it always did.
- **Right-click a thumbnail** for **Make this the cover** and **Remove from this item**. The cover is
  ringed in gold.
- Removing a picture never deletes the file. It stays in your images folder, ready to be dragged back
  on. Removing the cover promotes the next picture rather than leaving the card blank.
- A picture whose file has gone keeps its place in the row as a red **?**, because the one you need to
  remove is exactly the one that cannot be shown.

A card with more than one picture shows the count in the corner of its thumbnail. **Right-click the
picture** to open the viewer, which pages through the whole set with the **◄ ►** buttons or the arrow
keys, naming each file as it goes.

Duplicating an outfit, or making a variant of an item, copies the whole set along with the cover.

## Manual mode

**Manual mode**, on the session HUD and in Settings → Screenshots, stops a session
advancing by itself. It stays on the item in front of you for as many screenshots as you care to take:
the first becomes the card's picture and every one after it joins the set behind it.

Framing a piece well is usually a few attempts rather than one, and without this the session moves on the
instant the first screenshot lands — turning every misjudged angle into a re-shoot later.

- **Next Item** keeps what you have taken and moves to the next thing in the queue. It only appears when
  there is something to move on to, so a single-item shoot from an edit panel does not show it.
- **End Session** stops, keeping everything already filed.
- Taking no pictures of an item costs nothing: **Next Item** leaves it exactly as it was.

The HUD counts what you have taken of the current item, and the compact window shows the same. The
camera preset controls are there throughout, so you can jump between saved angles between shots.

Fire off as many as you like in a row. Each takes a moment to crop and save, and any that arrive while
one is being written wait their turn rather than being lost. A picture taken for an item the session has
already moved past is discarded rather than filed against whatever came next.

Turning it on mid-session takes effect from the next shot; turning it off hands the item back to the
automatic behaviour below.

## Extra angles in a session

This is the automatic alternative to [manual mode](#manual-mode) — useful when you want the same set of
angles from every item without deciding each time.

A session takes one shot per item by default. To have it photograph the side, the back or a close-up as
well, tick those [camera presets](#choosing-the-cover-and-the-extra-angles) — the session then waits
for a screenshot at each ticked angle in turn and files them as that item's other pictures.

Ticks count immediately, including on the item the session is already waiting on: what is left to shoot
is read from the preset list each time a picture lands rather than being fixed when the item was worn.

The HUD says which angle it is waiting for — *Shot 2 of 3 — Back* — and the camera has already moved
there. Two skips replace the one:

- **Skip Angle** gives up on this angle only and moves to the next one of the same item, for a back
  view of something with nothing on its back.
- **Skip Item** gives up on the item entirely, remaining angles included.

The angle's name goes into the file name, so a folder of pictures says which is which without opening
them. The cover keeps the plain name it always had.

Re-shooting an item replaces its pictures: the cover shot overwrites the cover, and the first new angle
to arrive clears the old angles out. End the session after the cover shot and the old angles are left
alone — nothing is thrown away for an angle that was never re-taken. The files themselves are never
deleted from your images folder.

A session over the whole wardrobe still only queues items with **no picture at all**, angles or not.
Use a [selection](#a-session-over-just-the-items-you-pick) to re-shoot items that already have one.

## Fully automatic sessions

> **Experimental.** It presses the game's own screenshot function, which is not an API, and it has
> not yet been run over a large wardrobe. The tick is in Settings → **Experimental**, at the bottom
> of the panel, and off until you turn it on. Watch the first few shots before leaving it to run.

**Fully automatic sessions**, in Settings → Experimental and as a tick on the session HUD, is manual
mode's opposite: the session takes the screenshots itself. Each item is worn, the camera moves to the
angle its slot asks for, the shot is taken, cropped and filed, and it moves on to the next. There is
nothing to press. Point it at a wardrobe of a few hundred pieces and go and do something else.

It is the ordinary [session](#screenshot-sessions) in every other way — the same queue, the same
stripping, the same base character, the same [extra angles](#several-pictures-per-item) — so it works
for a whole wardrobe, for [just the items you pick](#a-session-over-just-the-items-you-pick), for
outfits, and for a single item from its edit panel.

### Before you start one

- **Set your camera angles up.** Settings → Screenshots → **Edit Angles For Every Slot**. An automatic
  session has nobody to frame a shot, so a slot with no angle saved is photographed from wherever the
  camera happens to be standing. See [camera presets](#camera-presets).
- **Be in GPose.** Angles are written to the GPose camera and nowhere else. The session HUD says so if
  you are not, and the run will otherwise take every picture from the same place.
- **Check the screenshots folder.** Settings → Screenshots. A session finds its pictures by watching
  that folder, and an automatic one has no way of noticing that nothing is arriving except by giving up.

### The delay

**How long to wait before each shot**, so what is being photographed has settled. Raise it if pictures
come out mid-redraw or at the previous angle — a slower drive wants more, and there is no number that
is right for every machine. The first shot of each item is given longer again on top of it, since that
is the one that follows a redraw.

### While it runs

- **Pause** holds the run where it is, so you can move the camera. It carries on from the time the
  countdown had left rather than starting it over.
- **Shoot Now** takes the picture the session is waiting for immediately, without waiting out the
  countdown. It is there in every mode, manual included, where it saves reaching for the screenshot key
  at all.
- **Skip Angle**, **Skip Item** and **End Session** work exactly as they do in an ordinary session.

### When something goes wrong

A shot that produces no picture is asked for again, twice, before the session gives up on that angle and
moves on. If three go missing in a row, the run **pauses on the shot that failed** rather than carrying
on — which almost always means the screenshots folder is wrong, and is worth finding out about before
the whole wardrobe has been photographed into somewhere nobody is watching. The reason is written to the
log; open it with `/xllog` and look for lines beginning `[Wardrobe] Session`.

An automatic run writes what it is about to do to the log before it starts — how many targets, the
delay, whether you are in GPose, which slots have no angle saved, and both folders — and a line for
every shot it takes and every picture it files. Nobody is watching an unattended run, so that log is
the only record of it. It ends on a count of what was filed.

If the game refuses screenshots for half a minute — a cutscene, a loading screen — the run pauses
rather than asking forever. Press **Resume** when you are back.

Manual mode and fully automatic mode are opposites, so turning one on turns the other off.

## Screenshot sessions

A session queues every item that has no preview image, equips each in turn, and picks up new
screenshots from a watched folder automatically.

Options during a session:

- **fully automatic** — the session takes the screenshots itself, so nothing has to be pressed
- **per-slot GPose camera presets** — saved camera positions per equipment slot, as many as you want
- **strip other items** — persisted between sessions
- **base character** — the customisation mods and slots every shot keeps, chosen from the HUD
- **compact window mode** — keeps the plugin UI out of the shot

Stripping stops at the active [base character](Wearing-Items.md#base-character), which is re-applied
before every shot — so the hair, skin, tail and ear mods that make the character yours are in each
picture rather than stripped out of it.

Two things are set in Glamourer for you while a session runs, and put back as you had them when it ends:

- **Your weapon is hidden**, unless the item being photographed is the weapon.
- **Your hat is shown** while a head piece is being photographed. Hiding hats is a setting plenty of
  people leave on permanently, and without this a run over your headgear would produce a folder of
  pictures of a bare head. For every other slot the hat goes back to whatever you had.

If either could not be read from Glamourer it is left alone rather than forced to a guess.

**Screenshot Outfits** starts a session covering every outfit without a preview, exactly like the
item sessions. [Synced glamour plates](Outfits.md#vanilla-glamour-plates) are outfits like any other,
so they join the run and can be photographed one at a time from their edit panel as well — which is
much of the point of bringing them in.

### A session over just the items you pick

**View → Select Mode**, tick the items, then **Edit Selected → Screenshots → Photograph**. The
session then covers exactly those items, in place of the whole wardrobe.

This runs **everything you ticked**, including items that already have a preview — those get
replaced. That is the difference between the two: a session over the whole wardrobe photographs what
is *missing*, because that is the only sensible reading of "do the lot". A selection is you saying
which ones, and wanting to redo a preview you are unhappy with is most of the reason to make one.
The panel says how many of the selected already have an image before you start.

The selection survives the session, so a run that goes wrong can be repeated without ticking
everything again.

## The crop guide

A captured picture is centre-cropped to a square, so on a widescreen window most of what is on
screen is thrown away — and framing a character against the window centres them in an image that no
longer exists. The **crop guide** draws the part that survives and dims the rest, so the shape you
are composing for is the shape you can see. Thirds are marked inside it to compose against.

**Square captures only.** An outfit shot with [portrait previews](#portrait-outfit-previews) on is
cropped 9:16 and gets no guide, because GPose's own portrait mode already frames that shot and shows
you where to put the camera. A second frame over the top of it would be the worse of the two.

**Settings → Screenshots → Crop guide** has three answers:

| | |
| --- | --- |
| **Off** | Frame by eye, as it worked before |
| **During screenshot sessions** | On from the moment a session starts until it ends. The default |
| **Always** | On whenever the game is showing |

**View → Crop Guide** picks between its three modes without a trip to settings, and the
compact session view has the same button under its action row — framing is done from whichever of
those you are watching a session from. Switching it off and back on keeps the mode you chose, so
setting it to **Always** and then using the toggle does not quietly move you onto **During
sessions**.

The guide is never in the resulting picture. The shot comes from the game's own screenshot function,
which does not capture plugin windows — the same property that keeps the wardrobe's own window out
of your screenshots. **Confirmed by testing**, not assumed.

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

### Setting them up before a session

**Settings → Screenshots → Edit Angles For Every Slot** opens every slot's preset list in one panel. This
is the place to prepare a run: go into GPose once, work down the slots framing an angle for each, then
start the session and let it go.

Without it the only place to edit a slot's presets was a session that had already reached an item in that
slot — which is the wrong moment, because the session is sitting there waiting while you invent the angle.

Each slot's row says how many angles it has, how many shots a session will take for it, and how many
wardrobe items are in it. One slot opens at a time, and the controls inside are exactly the ones the
session HUD shows. The panel counts how many slots still have no angle, so you can see what is left to do,
and says so if you are not in GPose — saving an angle needs the GPose camera.

Slots with no items yet are listed too. A wardrobe grows, and an angle saved before the first pair of
boots arrives is exactly what this is for.

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

Renaming, deleting, changing which preset is the cover and ticking extra angles are left out on
purpose: they are housekeeping, and a stray click on a delete button between shots is a preset gone.
**Expand** is there when you want the full panel.

The compact window shows the same *Shot 2 of 3* line and the same two skip buttons as the HUD, since
this is the window most of a session is actually watched in.

### Choosing the cover and the extra angles

Each preset row starts with two controls:

- the **button on the left** picks the **cover angle** — the shot that becomes the card's picture, and
  the one a session loads first. Exactly one per slot.
- the **checkbox** beside it asks for an **extra shot** at that angle as well. Tick as many as you
  like; the session photographs the cover, then each ticked angle in turn. See
  [Extra angles in a session](#extra-angles-in-a-session).

The cover's own row has no checkbox: a session photographs that angle because it is the cover, and a
tick there would only ask for the same picture twice. Promoting a ticked preset to cover clears its
tick for the same reason.

Under the list, a line says how many shots a session will take for that slot and in what order, which
is the thing worth knowing before starting one.

Neither control changes the order, so you can arrange the list however you like and pick the cover
independently of it. Both travel with the presets file, so a shared set of angles arrives ready to
shoot.

> Presets saved before pan was supported don't store one, and applying them leaves the camera's pan
> untouched rather than guessing at a centred value. Press **Update** to capture it.

### Coming from a single preset per slot

Slots used to hold exactly one preset. Yours becomes **Preset 1**, ticked as its slot's default, and
that is the whole of it — sessions carry on using the same angle and nothing needs doing.

If one preset per slot is all you want, just never save a second.
