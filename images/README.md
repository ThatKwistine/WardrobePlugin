# Plugin icon

Drop the icon here as **`icon.png`**.

Requirements Dalamud enforces:

- **PNG**, square
- **512 × 512** is the expected size (smaller renders blurry in the installer; larger is wasted)
- Keep it well under 1 MB
- Readable at roughly 64 × 64, since that is the size the plugin installer actually draws it at

It is referenced in two separate places, both of which need to be right:

- `WardrobePlugin.json` and `pluginmaster.json` — `IconUrl`, pointing at the raw GitHub URL for
  `images/icon.png`. This is what the plugin installer displays before anything is downloaded.
- Inside `latest.zip` — the csproj copies `images/**` to the build output so DalamudPackager
  includes it.

There is no fallback: if the file is missing the installer just shows a blank tile, which is not
an error you will see in any log.
