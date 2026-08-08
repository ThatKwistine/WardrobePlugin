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

4. Write the release notes. They **open** with the **Installation** section, copied out of
   `README.md` word for word — see [Release notes](#release-notes) below.
5. Create a GitHub release and attach `latest.zip` as an asset. The download links in
   `pluginmaster.json` use `/releases/latest/download/latest.zip`, so they resolve to whatever
   the newest release is and never need editing.
6. Commit and push the updated `pluginmaster.json`. Dalamud reads it from the branch directly,
   so this is the step that actually publishes the update.

## Release notes

**Start with Installation, copied from `README.md`'s `## Installation` section word for word** — the
numbered `/xlsettings` steps, the fenced `pluginmaster.json` URL, and the closing line about updates
arriving through the plugin installer.

It goes first, before the list of changes, and it is copied rather than paraphrased. The plugin ships
through a custom repository rather than the official one, so a release page reaches people who have
no idea how to install it, and nobody lands on the README from a release link. Copying it verbatim
keeps the two from drifting into two different sets of instructions — read it out of the README at
release time rather than writing it from memory, since it is the source of truth and may have
changed.

Then the changes, under **What's new** and, where there are any, **Fixes**.

### Shape

**A flat bullet list under each heading. No sub-headings, ever.** One bullet per change: a bold
sentence naming it, then one or two plain sentences saying what it does. That is the whole format —
read the last two or three releases before writing and match them.

The pull is always towards writing more, because the work is fresh and every part of it feels worth
explaining. It is not. A release page is read in about twenty seconds by someone deciding whether to
click update, and `###` headings with paragraphs beneath them turn it into a documentation page
nobody asked for. The detail belongs in `docs/`, which is where anyone who wants it will look; the
notes exist to say what changed.

If a change needs the reader to *do* something before it takes effect — re-save a preset, press
Update, re-run a scan — give that its own bullet rather than burying it in the one above. It has been
its own bullet in every release where it came up, and it is the part people miss.

### Two things to hold to

- **Only claim what has been seen working.** A fix that is correct in code is not a user-visible
  improvement until someone has run it. Say what changed, not what it ought to feel like.
- **Never name whoever reported something.** Credit and thanks belong in the issue thread, not in the
  notes.

## Notes

- **Debug vs Release.** Dalamud loads the plugin from `bin/Debug/...` for local development
  (that path is recorded in `dalamudConfig.json`). Release exists to produce `latest.zip`.
  Building Release does not change what you are running in game, and vice versa.
- **`DalamudApiLevel`** must match the API level of the Dalamud people are running. When Dalamud
  bumps its API, the plugin will refuse to load until this is raised and it is rebuilt against
  the new libraries.
- **Testing channel.** `DownloadLinkTesting` currently points at the same zip as the stable link.
  Point it at a pre-release asset if you ever want testers on a separate build.
