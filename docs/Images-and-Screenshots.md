# Images and Screenshots

## Assigning an image by hand

Open **Images** in the toolbar, point Settings at a folder of images, then drag a thumbnail onto any
item or outfit card.

## Screenshot sessions

A session queues every item that has no preview image, equips each in turn, and picks up new
screenshots from a watched folder automatically.

Options during a session:

- **per-slot GPose camera presets** — a saved camera position per equipment slot
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

A preset stores the camera angle, tilt, both kinds of zoom, and the field of view, so a session can
frame each slot the same way every time.

FFXIV has **two separate zooms** and a preset captures both:

- **Scroll-wheel zoom**, the camera's distance from your character
- **Camera Distance in the Group Pose settings menu**, which despite the name is a field-of-view
  adjustment rather than a distance

Presets are written directly to the game's camera and re-applied for about half a second, because a
single write is overwritten by the game's own per-frame camera update.

They only work with the **native GPose camera**. If BRIO's free camera is active it drives the camera
independently and presets will have no visible effect.

> Presets saved before the Group Pose camera distance was supported store a neutral value for it, so
> they keep working unchanged. Hit **Update** on a slot to capture it.
