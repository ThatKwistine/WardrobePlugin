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
        new(new Version(1, 5, 4, 0), "24 August 2026", new List<ChangelogSection>
        {
            new("What's new", new List<ChangelogNote>
            {
                new("Fully automatic screenshot sessions. Experimental.",
                    "Settings → Experimental, or the tick on the session HUD. The session takes " +
                    "the screenshots itself: each item is worn, the camera moves to the angle its " +
                    "slot asks for, the shot is taken, cropped and filed, and it moves on to the " +
                    "next — the automatic session with the one keypress it still needed taken " +
                    "out of it. Point it at a wardrobe of a few hundred pieces and go and do " +
                    "something else.\n\n" +
                    "It is under Experimental because it presses the game's own screenshot " +
                    "function, which is not an API, and because nobody has yet run it over a " +
                    "large wardrobe. Off until you turn it on. Watch the first few shots."),

                new("A delay setting for it.",
                    "How long to wait before each shot, so what is being photographed has settled. " +
                    "Raise it if pictures come out mid-redraw or at the previous angle; a slower " +
                    "drive wants more. The first shot of each item is given longer again on top of " +
                    "it, since that is the one that follows a redraw."),

                new("Pause, and Shoot Now.",
                    "An automatic run can be held where it is so you can move the camera, and " +
                    "carries on from the time the countdown had left rather than starting it over. " +
                    "Shoot Now takes the picture the session is waiting for immediately, and is " +
                    "there in every mode — including manual, where it saves reaching for the " +
                    "screenshot key at all."),

                new("An automatic run stops rather than grinding on.",
                    "If three shots in a row are asked for and nothing is filed — which almost " +
                    "always means Settings → Screenshots is pointed at the wrong folder — it " +
                    "pauses on the shot that failed and says so in the log, instead of " +
                    "photographing the whole wardrobe into somewhere nobody is watching."),

                new("Plates and Designs stay as you leave them.",
                    "The two tick boxes over the outfits grid used to come back ticked at every " +
                    "login, so a wardrobe kept clear of twenty glamour plates had to be cleared " +
                    "again every time you opened the game. They are remembered now. Syncing " +
                    "plates ticks Plates back on if it added any."),

                new("Sync Plates moved up onto the toolbar row.",
                    "It had a line to itself directly under the row it belongs to. It now sits " +
                    "beside the tick boxes for the same cards, with its counts after it, and the " +
                    "notices about plates that have drifted or been emptied stay below the " +
                    "separator where they were."),
            }),

            new("Fixes", new List<ChangelogNote>
            {
                new("The leftover mods notice listed things you were wearing.",
                    "The notice that says N mods the wardrobe enabled are still on with nothing " +
                    "wearing them was built from the worn list, which is empty every time the " +
                    "game starts — so after " +
                    "a restart it named every mod the wardrobe had switched on, including the ones " +
                    "holding up the look on your character at that moment, and offered them all to " +
                    "Disable Them.\n\n" +
                    "Your character is now read first, every time, and anything genuinely on is " +
                    "recorded as worn before the list is built. What is left is what nothing in " +
                    "the wardrobe accounts for — usually an item since deleted, or one whose mod " +
                    "options have been changed in Penumbra since."),

                new("A scan could pass over an item you were wearing without a word.",
                    "A scan recorded what it found one slot at a time, and would not touch a slot " +
                    "the worn list already had an entry for — even when that entry named " +
                    "something the same scan had just proved was not on. The item actually on " +
                    "your character lost the slot to it, was never recorded as worn, and said " +
                    "nothing about it in the log: pressing Scan looked like it had worked and " +
                    "changed nothing.\n\n" +
                    "Most visibly, the mod behind it was then reported as a leftover after every " +
                    "restart, and no amount of scanning would clear it. Slots are now settled " +
                    "after the whole wardrobe has been looked at, so a stale entry gives way to " +
                    "the item that is really there, and the leftovers notice asks the scan " +
                    "directly rather than reading it off the worn list."),

                new("A customisation item can apply a Glamourer design with it.",
                    "A face sculpt replaces the files of one particular face, so on a character " +
                    "set to any other it is enabled, correct and completely invisible. Hair has " +
                    "never had this problem — the wardrobe reads the hairstyle number out of the " +
                    "mod and switches you to it — but a face has no single number to read: what " +
                    "makes a sculpt look right is the face together with the skin, eye and hair " +
                    "colouring around it.\n\n" +
                    "So a hair, face, tail, ear or skin item can now name a Glamourer design, " +
                    "applied whenever it goes on. Its customisations only — its gear and its " +
                    "hairstyle are each a tick box, and both are off. The hairstyle " +
                    "especially: every design carries one, and a design applied for its face " +
                    "would otherwise switch you off the hairstyle your hair mod needs and " +
                    "leave that mod enabled and invisible. A live link, not a copy: edit the " +
                    "design in Glamourer and the next apply has the edit.\n\n" +
                    "Taking the item off does not undo it. Settings → Revert customisation mods " +
                    "to is what puts your own face back, as it already was for these mods."),

                new("Ctrl on In-Game Look leaves the base character off.",
                    "Unequip All has always taken a Ctrl-held press to take the base off " +
                    "with everything else. In-Game Look now does the same, on both the " +
                    "toolbar button and the one in a glamour plate’s panel.\n\n" +
                    "The setting beside it is the standing answer; this is the one-off, " +
                    "for when you want to see what you really look like without changing " +
                    "a setting you will only have to change back. The status line says so " +
                    "when the base was left off."),

                new("Any design the wardrobe applies can leave your hairstyle alone.",
                    "Every Glamourer design carries a hairstyle whether or not it was saved " +
                    "for one. A hair mod only replaces one hairstyle's files — so a design " +
                    "applied for something else entirely, a base character's colouring or an " +
                    "outfit's body, would switch you off the hairstyle your hair mod needs " +
                    "and leave it enabled, correct and invisible.\n\n" +
                    "Apply its hairstyle too now sits beside Apply its gear too on base " +
                    "characters, outfits, the revert design in Settings, and customisation " +
                    "items. On everywhere it existed before, so nothing changes until you " +
                    "turn it off; off by default on the new per-item designs, which are " +
                    "applied for a face and have no business touching hair.\n\n" +
                    "One exception, whatever the setting says: taking a hair mod off always " +
                    "brings the revert design's hairstyle with it. Otherwise you would be " +
                    "left on the hairstyle the mod replaced with the mod switched off."),

                new("It warns when a design and a customisation mod disagree.",
                    "A face mod replaces the files of particular face numbers, so a design " +
                    "setting any other face leaves it enabled, correct and invisible — which " +
                    "is the failure the design was picked to prevent. The edit panel now " +
                    "says so under the picker, and the log says so when the item is worn.\n\n" +
                    "Checked against every face the mod covers for your race rather than " +
                    "one number, because mods routinely cover several at once: option " +
                    "groups let a single mod ship f0001 through f0004, often across more " +
                    "than one race. It also says when a mod has no files for your race at " +
                    "all.\n\n" +
                    "Tail and Viera ear mods are checked the same way, against the tail " +
                    "shape — one customisation is the tail on races that have one and the " +
                    "ears on Viera. Skin and hair are checked against your race only: a " +
                    "skin mod’s files are b0001 for everybody, and a hair mod’s number is " +
                    "set from the mod itself rather than left to a design.\n\n" +
                    "It stays quiet where it cannot answer honestly — a design that sets no " +
                    "such number, a race it cannot read, or an item imported before this " +
                    "existed. Press Re-detect on one of those and the result says what the " +
                    "mod replaces."),

                new("A face sculpt and a face texture can be worn at once.",
                    "Customisation slots were exclusive: one Face item at a time, one Hair, one " +
                    "Skin. But a sculpt and the texture painted on it are not alternatives to " +
                    "each other, so applying one took the other off and there was no way to have " +
                    "both.\n\n" +
                    "Each customisation item now carries a Layer — sculpt for a mod that ships a " +
                    "model, texture for one that ships only textures — and only items sharing a " +
                    "layer displace each other. Two sculpts still swap, as they should. It is " +
                    "free text, so a mod the detection has no word for can be given one: type " +
                    "lashes into two items and only those two will ever replace each other.\n\n" +
                    "Items imported before this have a blank layer and still take the whole slot, " +
                    "which is what they did before — filling them all in would have left two " +
                    "sculpts enabled at once. Press Re-detect on one to fill it in."),

                new("Outfits is a view, not a filter.",
                    "It used to toggle, which let it and All both look like the current view at " +
                    "once, and let a slot button quietly change the filter under an outfits grid " +
                    "that stayed where it was. Pressing Outfits now goes to outfits, and pressing " +
                    "All or any slot goes to items — one of them is lit at a time. Favourites, " +
                    "Worn and Variants are unchanged: those narrow whichever view you are in."),

                new("An outfit's headgear and weapon toggles were undone a moment after being set.",
                    "An outfit set to hide the headgear applied it correctly and then lost it: " +
                    "wearing the outfit asks for a character redraw, that redraw finishes a frame " +
                    "or two later, and the hat came back with it while Glamourer still had it " +
                    "recorded as hidden — so asking again changed nothing, because nothing about " +
                    "the value had changed. Both toggles are now written again after each redraw, " +
                    "the same as the outfit's items and dyes already were.\n\n" +
                    "A screenshot of an outfit ignored them too, using whatever you had set " +
                    "before the session instead. The outfit's own answer is used where it has " +
                    "one; where it says leave alone, nothing changes."),
            }),

            new("Worth doing once", new List<ChangelogNote>
            {
                new("Set your camera angles up before running one.",
                    "Settings → Screenshots → Edit Angles For Every Slot. An automatic session " +
                    "has nobody to frame a shot, so a slot with no angle saved is photographed from " +
                    "wherever the camera happens to be standing — and it needs the GPose camera, " +
                    "so start the run in GPose. The session HUD says so if you are not."),
            }),
        }),

        new(new Version(1, 5, 3, 2), "20 August 2026", new List<ChangelogSection>
        {
            new("What's new", new List<ChangelogNote>
            {
                new("An uncheck all button on the import panel's slot list.",
                    "Beside \"Items to create\", for a mod covering several slots when you want " +
                    "one of them. It turns into check all once everything is off."),

                new("The tag tree can be used when editing an item.",
                    "Settings → Tag Picker. The edit panel offers existing tags as a row of " +
                    "buttons; the import panels offer the same tags as a tree you can open. " +
                    "Either can now be used in the editor. The row narrows as you type and " +
                    "right-clicks into the box for editing; the tree shows nesting, which the row " +
                    "cannot — it has space for the last part only. Off unless you turn it on."),

                new("Re-detect says what it found.",
                    "It reported only to the log before, so a press that found nothing looked " +
                    "exactly like one that worked. It now writes the answer under the button, and " +
                    "when the mod's files are for a different slot than the item is on, it names " +
                    "that slot."),
            }),

            new("Fixes", new List<ChangelogNote>
            {
                new("Importing a mod with two option groups of the same name closed the window.",
                    "Nothing stops a mod having two groups with one name, and one shipped two " +
                    "called 3D OPTIONS. The wardrobe assumed names were unique and threw when " +
                    "they were not, which in the middle of drawing takes the whole panel down. " +
                    "Both groups are read now. They still share one set of options, because " +
                    "Penumbra identifies a group by its name too."),

                new("Mods saved in Penumbra's newer folder layout looked empty.",
                    "That layout keeps the mod's always-on files somewhere the wardrobe was not " +
                    "reading, so a mod with no option groups came back as touching nothing: no " +
                    "slot, no game item, and an item created on Head whatever it really was. A " +
                    "mod's age has nothing to do with it — the layout comes from whichever " +
                    "version of Penumbra last wrote the folder."),

                new("Skin and body texture mods were detected as nothing.",
                    "The file paths in them are invented by the mod's author rather than named " +
                    "after anything in the game, so nothing in one says what it is. A mod made of " +
                    "nothing else is now taken for a skin, which covers body textures, tattoos, " +
                    "scars and body writing. Mods built with Proteus are read as well: their " +
                    "options list no files at all, and the paths are kept in the tool's own file."),

                new("Mods that swap a file rather than add one.",
                    "A swap points one of the game's paths at another and is how a fair number of " +
                    "mods work. Only added files were being counted, so a mod built out of swaps " +
                    "read as empty."),

                new("Re-detect answered about the slot the item was saved with.",
                    "Changing Slot and pressing Re-detect kept reporting on the old slot, since " +
                    "the box only takes effect on Save — so an item on the wrong slot could not " +
                    "be put right without saving first. Re-detect now moves the item to the slot " +
                    "in the box and detects for that one."),
            }),

            new("Worth doing once", new List<ChangelogNote>
            {
                new("Press Re-detect on anything that says no game item was detected.",
                    "Items imported before this keep whatever they were given at the time. Open " +
                    "the item, and if its Slot is wrong — Head is what an undetected mod was " +
                    "given — set the right one before pressing Re-detect, which now follows it."),

                new("Mods that imported with no slots detected can be imported again.",
                    "Skin mods, mods built with Proteus and anything in the newer folder layout " +
                    "come back with their slot filled in now."),
            }),
        }),

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
