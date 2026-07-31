# Releasing

The plugin is distributed through a custom Dalamud repository: a single `pluginmaster.json`
hosted on GitHub, which friends add to Dalamud themselves.

## One-time setup

1. Add `images/icon.png` (see `images/README.md`).
2. Create the GitHub repo `ThatKwistine/WardrobePlugin` and push this project to it.
   If the repo ends up under a different name, update every URL in `pluginmaster.json`,
   `WardrobePlugin.json` and this file to match.
3. Tell people to add this URL in
   **Dalamud Settings → Experimental → Custom Plugin Repositories**:

   ```
   https://raw.githubusercontent.com/ThatKwistine/WardrobePlugin/main/pluginmaster.json
   ```

   After **Save and Close**, "Wardrobe" appears in the normal plugin installer.

## Cutting a release

1. Bump the version in **both** places — they must match, and must be higher than the last
   release or Dalamud will not offer an update:
   - `WardrobePlugin.csproj` → `<Version>`
   - `WardrobePlugin.json` → `AssemblyVersion`
2. Update `pluginmaster.json` → `AssemblyVersion` and `TestingAssemblyVersion` to the same value.
3. Build for distribution:

   ```
   dotnet build --configuration Release
   ```

   This produces `bin/Release/net10.0-windows/WardrobePlugin/latest.zip`.

4. Create a GitHub release and attach `latest.zip` as an asset. The download links in
   `pluginmaster.json` use `/releases/latest/download/latest.zip`, so they resolve to whatever
   the newest release is and never need editing.
5. Commit and push the updated `pluginmaster.json`. Dalamud reads it from the branch directly,
   so this is the step that actually publishes the update.

## Notes

- **Debug vs Release.** Dalamud loads the plugin from `bin/Debug/...` for local development
  (that path is recorded in `dalamudConfig.json`). Release exists to produce `latest.zip`.
  Building Release does not change what you are running in game, and vice versa.
- **`DalamudApiLevel`** must match the API level of the Dalamud people are running. When Dalamud
  bumps its API, the plugin will refuse to load until this is raised and it is rebuilt against
  the new libraries.
- **Testing channel.** `DownloadLinkTesting` currently points at the same zip as the stable link.
  Point it at a pre-release asset if you ever want testers on a separate build.
