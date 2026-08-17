using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace WardrobePlugin.Services;

/// <summary>One change, written the way the release notes write one: a name, then what it does.</summary>
public sealed record ChangelogNote(string Title, string Detail);

/// <summary>A heading and the notes under it — "What's new", "Fixes", and so on.</summary>
public sealed record ChangelogSection(string Heading, IReadOnlyList<ChangelogNote> Notes);

/// <summary>Everything that changed in one version.</summary>
public sealed record ChangelogEntry(Version Version, string Date, IReadOnlyList<ChangelogSection> Sections);

/// <summary>
/// What changed in each version, shown in game the first time that version runs.
/// </summary>
/// <remarks>
/// Kept in code rather than read from the release page. A release page is written for someone
/// deciding whether to install; this is read by someone who already has, so it opens with the
/// changes instead of the installation steps and says "you will need to re-save that" where the
/// release notes would say it in passing.
/// <para>
/// <b>This list is part of cutting a release.</b> Nothing checks that it was updated — a version
/// with no entry simply shows nothing — so it is in the release checklist rather than left to
/// memory. See <c>docs/Releasing.md</c>.
/// </para>
/// </remarks>
public static class Changelog
{
    /// <summary>The running plugin's version, as its assembly reports it.</summary>
    /// <remarks>
    /// Read from the assembly rather than repeated here, so it cannot disagree with the build. The
    /// csproj's <c>Version</c> is what sets it, and that is one of the fields a release bumps.
    /// </remarks>
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>Newest first, which is the order they are shown in.</summary>
    public static readonly IReadOnlyList<ChangelogEntry> Entries = new List<ChangelogEntry>
    {
        new(new Version(1, 5, 1, 3), "17 August 2026", new List<ChangelogSection>
        {
            new("Fixes", new List<ChangelogNote>
            {
                new("Hair, face and skin mods that needed a redraw to appear.",
                    "Switching a mod on does not reload what is already drawn on you. Plenty of " +
                    "these showed up on their own; the ones that did not went on correctly and " +
                    "stayed invisible until something redrew you, which is why redrawing by hand " +
                    "in Penumbra was the fix. Applying one now does that redraw for you."),
            }),

            new("What's new", new List<ChangelogNote>
            {
                new("Redraw on apply, per item.",
                    "In the edit panel and on the import panel, for items with no game item behind " +
                    "them. On for hair, face, tail, Viera ears, skin and other; off for " +
                    "animations, VFX and mounts, which are not on your character. Turn it off for " +
                    "a mod that shows up without it."),
            }),
        }),

        new(new Version(1, 5, 1, 2), "12 August 2026", new List<ChangelogSection>
        {
            new("Fixes", new List<ChangelogNote>
            {
                new("Camera presets appear while photographing an outfit.",
                    "The session window showed none at all for an outfit — only the compact view " +
                    "had them, so the angle could be changed only in the smaller of the two " +
                    "windows. Both now offer the outfit preset set."),
            }),
        }),

        new(new Version(1, 5, 1, 1), "12 August 2026", new List<ChangelogSection>
        {
            new("Fixes", new List<ChangelogNote>
            {
                new("Saving one item's mod options no longer rewrites the others.",
                    "Items sharing a mod across slots only keep each other in step on the option " +
                    "groups the other item's slot is actually part of, and only those groups — " +
                    "everything else it had set is left alone. Editing the feet no longer hands " +
                    "the legs, the wrists and both variants of a top a copy of the feet's options."),

                new("A group set to Ignore all is remembered as left alone.",
                    "It used to be stored as nothing at all, which reads back the same as never " +
                    "having set it — so the panel showed the whole group as off next time, and " +
                    "saving again made that true."),
            }),

            new("What's new", new List<ChangelogNote>
            {
                new("In-Game Look, in the toolbar.",
                    "Beside Strip. Takes the wardrobe's clothes off and clears Glamourer, so the " +
                    "character shows exactly what the game has on them — your real gear and " +
                    "glamour, including any plate you have applied. Your base character stays on " +
                    "unless you say otherwise in Settings → Base character."),

                new("What changed, shown in game after each update.",
                    "This window. Turn it off, or open it again for any version, in " +
                    "Settings → Changelog."),

                new("Advanced dyes are ready, if you want them.",
                    "An outfit can keep Glamourer's colour-row edits for each of its pieces — far " +
                    "past the game's two dye channels — and put them back whenever you wear it. " +
                    "They have been out of testing since this version and are worth a look if you " +
                    "have never turned them on: Settings → Advanced dyes. Off until you do, since " +
                    "they reach into Glamourer's own data."),
            }),

            new("Worth doing once", new List<ChangelogNote>
            {
                new("Re-set any group you had already set to Ignore all.",
                    "Those were stored as a real off, and there is no way to tell now which ones " +
                    "you meant as ignore. Set them once on this version and they will stay."),
            }),
        }),

        new(new Version(1, 5, 1, 0), "11 August 2026", new List<ChangelogSection>
        {
            new("What's new", new List<ChangelogNote>
            {
                new("Base characters — what a strip strips down to.",
                    "Settings → Base character holds the slots a strip never empties, the items it " +
                    "always wears, and a Glamourer design whose customisations it applies. The tail " +
                    "on a ring stays on while everything else comes off."),

                new("Screenshot sessions put your base character back before every shot.",
                    "Not just the first, so a long session ends on the same character it started " +
                    "with. The picker is on the session HUD as well as in Settings."),

                new("Your glamour plates, in the wardrobe.",
                    "Sync Glamour Plates reads the game's own twenty plates in as read-only " +
                    "outfits. Open the Glamour Plate window at a bell, an inn or the Dresser " +
                    "first — that is when the game sends them."),

                new("Applying a plate for real, from the wardrobe.",
                    "Apply In Game does what the Gear Set List does. The wardrobe then takes its " +
                    "own clothes off so you can see the result, and puts your base character back " +
                    "on over the top."),

                new("Camera presets follow your character's facing.",
                    "A preset saved as a face close-up used to show a profile if the character " +
                    "happened to be standing a different way."),

                new("Portrait outfit previews, and camera presets for outfits.",
                    "Settings → Screenshots → Portrait outfit previews matches gpose's own portrait " +
                    "mode. Outfits get one shared preset set of their own, and a right-click " +
                    "full-size view like item cards have."),
            }),

            new("Worth doing once", new List<ChangelogNote>
            {
                new("Press Update on a camera preset to give it the new angle.",
                    "Presets saved before this keep working exactly as they did, on the old " +
                    "absolute angle, until they are re-saved."),
            }),
        }),
    };

    /// <summary>
    /// Entries newer than a version the user has already seen, newest first.
    /// </summary>
    /// <remarks>
    /// A range rather than just the newest, so skipping two updates shows both rather than the
    /// second one silently swallowing the first. An unparseable or empty <paramref name="since"/>
    /// means nothing has been seen, and everything up to the running version is offered.
    /// <para>
    /// Never anything newer than what is running. An entry is usually written while its release is
    /// being prepared, so for a while the list describes a version this build is not — and announcing
    /// changes that are not in the running plugin is worse than announcing nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChangelogEntry> Since(string since)
    {
        Version.TryParse(since, out var seen);
        return Entries.Where(e => e.Version <= Current && (seen == null || e.Version > seen)).ToList();
    }
}
