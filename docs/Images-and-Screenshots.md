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
- **compact window mode** — keeps the plugin UI out of the shot

**Screenshot Outfits** starts a session covering every outfit without a preview, exactly like the
item sessions.

## Previews are always square

Screenshot sessions crop the largest centred square out of the shot and save it at 512×512, so
nothing is distorted.

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
- **×** deletes it

Right-click the name to **Rename**.

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
