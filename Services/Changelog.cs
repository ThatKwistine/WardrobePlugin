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
        new(new Version(1, 5, 3, 1), "19 August 2026", new List<ChangelogSection>
        {
            new("Fixes", new List<ChangelogNote>
            {
                new("Mods stopped being switched off after turning on the collection setting.",
                    "Turning on \"use whichever collection my character is on\" lost track of every " +
                    "mod the wardrobe had already enabled, so removing an item left its mods " +
                    "running and the scan filled up with items whose mods Glamourer was not " +
                    "showing. Nothing had to be changed for it but the setting itself. The older " +
                    "records are found again, and are switched off in the collection they were " +
                    "switched on in — including by Disable Their Mods, which had the same fault."),
            }),

            new("What's new", new List<ChangelogNote>
            {
                new("Tick boxes for plates and designs in the outfits grid.",
                    "On the row above the grid, both ticked, each shown only when you have that " +
                    "kind of card. Untick them to look past twenty glamour plates at the outfits " +
                    "you built yourself."),
            }),
        }),

        new(new Version(1, 5, 3, 0), "19 August 2026", new List<ChangelogSection>
        {
            new("Fixes", new List<ChangelogNote>
            {
                new("Hair mods that never switched the hairstyle.",
                    "If you play a Hyur, Elezen or Miqo'te, or a male Roegadyn, hairstyle " +
                    "switching has quietly done nothing since it was added: the race was looked " +
                    "up in a format it was never saved in, so the mod was refused as not covering " +
                    "your race while the log listed the very number it had failed to find. It " +
                    "works now, on the items you already have."),

                new("Hair mods on a character whose race is set in Glamourer.",
                    "The race came from the game, which still says who you were before Glamourer " +
                    "redrew you — so a hair built for the race you are now was turned away. " +
                    "Glamourer is asked first."),

                new("The search box only searched the item grid.",
                    "Typing while looking at outfits did nothing at all. Outfits now match on " +
                    "their name, tags and pictures' worth of contents — including the mods behind " +
                    "their items, so searching a mod finds the outfit built out of it."),

                new("Searching for more than one word.",
                    "The whole box had to appear, in that order, inside a single field, so " +
                    "\"black boots\" could not find \"Black Leather Boots\". Every word is now " +
                    "looked for separately and each may land in a different place. Mods are " +
                    "matched on their folder as well as their name, and an empty grid says which " +
                    "filter is narrowing it rather than blaming the search."),

                new("The base character not coming back when you took something off.",
                    "Removing a hair mod worn over a base whose own hair is a mod left you in " +
                    "your vanilla hair. The base is put back on any removal now, as it always was " +
                    "on a strip."),

                new("Camera presets applied outside GPose tilted your camera.",
                    "Outside GPose that is the camera you walk around with, and one of the values " +
                    "a preset writes is the third person camera angle from your own game " +
                    "settings, which stays where it is put. Presets now only apply in GPose."),
            }),

            new("What's new", new List<ChangelogNote>
            {
                new("Use whichever collection your character is on.",
                    "Settings → Collection. For anyone whose collection changes with the " +
                    "character they are playing — a wardrobe built on one used to enable its mods " +
                    "in a collection the others never read. Worn ticks follow the collection, and " +
                    "mods left on in a collection you have left are now listed, with the choice " +
                    "of switching them off or keeping them. A base character can name a " +
                    "collection too, so changing character changes which base is active."),

                new("Unequip All empties every slot properly.",
                    "It sets each slot to Nothing rather than to an invisible item, and reaches " +
                    "slots no wardrobe item was in. It keeps your base character; hold Ctrl to " +
                    "take that off as well. Strip is unchanged and always strips down to the base."),

                new("Headgear and weapon, per outfit.",
                    "An outfit can show or hide either when you wear it — a hood is part of the " +
                    "look, and so is putting the sword away for a dress. Outfits you already have " +
                    "leave both alone, which is also what a new outfit does until you say otherwise."),

                new("A base character's design can dress you too.",
                    "Off by default. For a base that is partly gear — nails on the hands slot, a " +
                    "piece worn as skin — which customisations alone cannot describe. A base can " +
                    "also keep its design applied after every redraw rather than only on a strip."),

                new("Camera diagnostics.",
                    "Settings → Screenshots. Off unless you are chasing a bug: it logs the " +
                    "camera's fields when a preset is saved, and what the preset kept from them, " +
                    "which is how an angle that does not come back shows what it is missing."),
            }),

            new("Worth doing once", new List<ChangelogNote>
            {
                new("Nothing, if your hair mods were already working.",
                    "The hair fixes apply to items you have already imported — there is no " +
                    "re-detect to press this time. If a hair mod still puts on the wrong style, " +
                    "that is the older problem and Re-detect in its edit panel is the fix."),
            }),
        }),

        new(new Version(1, 5, 2, 0), "17 August 2026", new List<ChangelogSection>
        {
            new("What's new", new List<ChangelogNote>
            {
                new("Your Glamourer designs, in the outfits grid.",
                    "Settings → Glamourer Designs turns them on. It is a live link rather than a copy: " +
                    "a design saved in Glamourer appears by itself, a rename follows through, and " +
                    "deleting it there removes the card. Wearing one applies the design and then any " +
                    "wardrobe items you attach to it — which is how the mods that belong with a look " +
                    "get enabled, since a design knows nothing about Penumbra. Cards take pictures, " +
                    "tags and dyes like any other outfit."),

                new("Manual mode for screenshot sessions.",
                    "The session stays on each item for as many screenshots as you want to take. The " +
                    "first becomes the card's picture and the rest join it, and it moves on when you " +
                    "press Next Item. Framing a piece well is usually a few attempts rather than one."),

                new("Several pictures per item and outfit.",
                    "A Pictures row in the edit panel holds as many as you like: drag them in from the " +
                    "image browser, right-click to choose the cover or take one off. Cards show the " +
                    "count, and right-clicking the picture pages through them all."),

                new("A shot at each camera angle you tick.",
                    "Tick any camera preset and a session photographs that angle too, on top of the " +
                    "cover — the side, the back, a close-up. The angle's name goes in the file name."),

                new("Every slot's camera angles in one panel.",
                    "Settings → Screenshots → Edit Angles For Every Slot, for framing a whole " +
                    "wardrobe's angles in one visit to GPose instead of inventing each one while a " +
                    "session waits."),

                new("Newest installed first, when importing.",
                    "A tick inside the mod picker lists Penumbra's mods with the most recently " +
                    "installed at the top, for importing something you have just downloaded."),

                new("Your hat is shown while a head piece is photographed.",
                    "Hiding hats is a setting plenty of people leave on permanently, and it was " +
                    "turning a run over your headgear into a folder of bare heads. Put back as you " +
                    "had it when the session ends, like the weapon already was."),
            }),

            new("Fixes", new List<ChangelogNote>
            {
                new("Hair mods that put on the wrong hairstyle.",
                    "Which hairstyle a mod replaces is now read from its model, which is the one file " +
                    "that can only belong to that hairstyle. It used to come from whichever file was " +
                    "read first, and hair mods routinely ship textures belonging to other hairstyles " +
                    "— so the wardrobe would switch you to a hairstyle the mod does not touch and the " +
                    "mod appeared to do nothing."),

                new("Screenshots taken in quick succession are no longer lost.",
                    "Each takes a moment to crop and save, and any that arrived during that were " +
                    "dropped. They wait their turn instead — which matters most in manual mode."),

                new("The session's buttons sit on one row.",
                    "Skip and End Session were on separate lines at different widths, looking like " +
                    "two unrelated controls rather than the choices they are."),
            }),

            new("Worth doing once", new List<ChangelogNote>
            {
                new("Press Re-detect on a hair mod that has been putting on the wrong hair.",
                    "Items already imported keep the hairstyle number they were given. Re-detect in " +
                    "the item's edit panel re-reads the mod and stores the right one. Only the mods " +
                    "that were actually wrong need it."),
            }),
        }),

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
