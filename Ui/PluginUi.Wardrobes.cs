using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Models;

namespace WardrobePlugin.Ui;

/// <summary>
/// Per-character wardrobes: the switcher, the notice that asks about a new character, and the
/// settings that manage them.
/// </summary>
/// <remarks>
/// The feature is off until asked for, and off there is one wardrobe bound to nobody — which is
/// what every wardrobe was before this existed. Nothing here draws at all in that state except the
/// settings section that turns it on.
/// </remarks>
public partial class PluginUi
{
    private string _newProfileName = string.Empty;

    // ── The notice ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks what to do about a character with no wardrobe of their own.
    /// </summary>
    /// <remarks>
    /// Above the grid with the other notices rather than in a popup. Nothing has changed yet and
    /// nothing needs answering this second — the wardrobe you were on is still the wardrobe you are
    /// on, and the question can sit there until it suits.
    /// </remarks>
    private void DrawProfileOffer()
    {
        if (!_config.PerCharacterWardrobes) return;
        if (_profiles.Pending is not { } who) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f),
            $"{who.Name} has no wardrobe yet.");

        Hint($"Still showing '{_config.ActiveProfile.Name}'.",
            "Nothing has switched. Pick one of these, or leave it — you will be\n" +
            "asked again next time you log in on this character.");

        ImGui.Spacing();

        if (ImGui.Button($" Use '{Shorten(_config.ActiveProfile.Name)}' "))
            _profiles.BindPendingToActive();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Binds {who.Name} to the wardrobe you are already on, so logging in\n" +
                             "as them goes straight to it from now on.\n\n" +
                             "For a second character who wears the same things.");

        UiLayout.SameLineIfRoomForButton($" New wardrobe for {who.Name} ");
        if (ImGui.Button($" New wardrobe for {who.Name} "))
            _profiles.CreateForPending();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Starts an empty wardrobe for {who.Name} and switches to it.\n\n" +
                             "Your tags and styles are shared, so it is empty of items rather\n" +
                             "than empty of everything. Items can be copied over afterwards.");

        UiLayout.SameLineIfRoomForButton(" Not Now ");
        if (ImGui.Button(" Not Now "))
            _profiles.DismissPending();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Leaves things as they are. Asked again on the next login.");

        ImGui.Spacing();
        ImGui.Separator();
    }

    /// <summary>Trims a wardrobe name to something that fits on a button.</summary>
    private static string Shorten(string name) =>
        name.Length <= 24 ? name : name[..23] + "…";

    // ── First-time setup ──────────────────────────────────────────────────────

    /// <summary>
    /// The setup step that asks whether this is one character's wardrobe or several.
    /// </summary>
    /// <remarks>
    /// Here rather than left to be discovered in Settings, because it is the one decision that is
    /// far easier made now than later: turning it on with three hundred items already imported
    /// means deciding, piece by piece, which of them belonged to whom. Answered on the first run it
    /// costs a tick box.
    /// <para>
    /// Directly after the collection step, which asks the same question in Penumbra's words —
    /// whether what this plugin touches belongs to one character or to several.
    /// </para>
    /// </remarks>
    private void DrawOnboardWardrobes()
    {
        ImGui.TextUnformatted("Do you play more than one character?");
        ImGui.Spacing();
        ImGui.TextWrapped("The wardrobe can keep a separate set of items, outfits and base " +
                          "characters for each character you play, and switch between them as you " +
                          "log in. Tags and styles stay shared, so a scheme is built once and means " +
                          "the same thing everywhere.");
        ImGui.Spacing();
        ImGui.TextDisabled("Leave this off if you dress one character, or if you would rather have " +
                           "everything in one place.");
        ImGui.Spacing();

        var on = _config.PerCharacterWardrobes;
        if (ImGui.Checkbox("Give each character their own wardrobe##onboard", ref on))
        {
            _config.PerCharacterWardrobes = on;

            // Claim the character who is setting this up, so the very first login after setup is
            // not met with a question about a wardrobe they have only just made
            if (on) BindCurrentToActiveWardrobe();

            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Nothing is divided up. You carry on with the one wardrobe you have,\n" +
                             "and each new character is offered one of their own the first time\n" +
                             "you log in as them.\n\n" +
                             "It never switches without asking.");

        if (!on)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Can be turned on later in Settings, though it is tidier now — " +
                               "afterwards, sorting a wardrobe out means deciding piece by piece " +
                               "which character each item belonged to.");
            return;
        }

        ImGui.Spacing();

        if (_profiles.CurrentCharacter is { } who)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.6f, 1f),
                $"This wardrobe is {who.Name}'s.");
            ImGui.Spacing();
            ImGui.TextDisabled("Everything you import from here on is theirs. Log in as somebody " +
                               "else and the wardrobe will offer them one of their own, or let them " +
                               "share this one.");
        }
        else
        {
            WarnHint("Not logged in, so this wardrobe is not bound to anyone yet.",
                "No harm done. The first character you log in as will be offered it,\n" +
                "and taking the offer binds it to them.");
        }
    }

    /// <summary>
    /// Binds whoever is logged in to the wardrobe in force, and names it after them if it is still
    /// called what it was called when it was made.
    /// </summary>
    /// <remarks>
    /// The rename only happens on the untouched default. Somebody who has already named their
    /// wardrobe meant that name, and having setup quietly replace it with a character name would
    /// be the plugin overruling a decision it asked for.
    /// </remarks>
    private void BindCurrentToActiveWardrobe()
    {
        if (_profiles.CurrentCharacter is not { } who) return;

        var profile = _config.ActiveProfile;
        profile.Bind(who.Name, who.World);

        if (profile.Name == "My Wardrobe") profile.Name = who.Name;
    }

    // ── The switcher ──────────────────────────────────────────────────────────

    /// <summary>
    /// The wardrobes, on the Character menu, with the one in force ticked.
    /// </summary>
    /// <remarks>
    /// Only drawn once the feature is on. With one wardrobe there is nothing to switch between, and
    /// a menu offering a choice of one is a menu that teaches nothing.
    /// </remarks>
    private void DrawProfileMenu()
    {
        if (!_config.PerCharacterWardrobes) return;
        if (!ImGui.BeginMenu("Wardrobe")) return;

        foreach (var profile in _config.Profiles.ToList())
        {
            var active = profile.Id == _config.ActiveProfileId;
            var bound  = profile.Characters.Count > 0
                ? string.Join(", ", profile.CharacterNames())
                : string.Empty;

            if (ImGui.MenuItem(profile.Name, bound, active) && !active)
                _profiles.SwitchTo(profile, "picked from the menu");

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"{profile.Items.Count} item(s), {profile.Outfits.Count} outfit(s).\n\n" +
                    (bound.Length > 0
                        ? $"Loads by itself for: {bound}."
                        : "Bound to no character, so it is only ever chosen by hand."));
        }

        ImGui.Separator();

        if (MenuAction("Manage Wardrobes…", "Rename, bind and delete wardrobes in Settings."))
        {
            _showSettings     = true;
            _settingsCategory = null;
            _settingsSearch   = "wardrobe";
        }

        ImGui.EndMenu();
    }

    // ── Copying between wardrobes ─────────────────────────────────────────────

    /// <summary>What the last copy did, shown until the next one.</summary>
    private string _copyStatus = string.Empty;

    /// <summary>True when there is anywhere to copy to.</summary>
    /// <remarks>
    /// Every entry point checks this rather than drawing a disabled control. With the feature off,
    /// or with one wardrobe, copying between wardrobes is not a thing that exists — and an item
    /// menu that lists an action you can never take is worse than one that does not mention it.
    /// </remarks>
    private bool CanCopyBetweenWardrobes =>
        _config.PerCharacterWardrobes && _config.Profiles.Count > 1;

    /// <summary>
    /// "Copy to wardrobe" as a submenu listing every wardrobe but the one in force.
    /// </summary>
    /// <remarks>
    /// Shared by the card's right-click menu and the bulk panel, so one item and forty behave
    /// identically and read the same. The copies are templates: they arrive with the same mods and
    /// options, and the point is to open them there and change what the other character needs.
    /// </remarks>
    private void DrawCopyToWardrobeMenu(IReadOnlyList<WardrobeItem> items, string id)
    {
        if (!CanCopyBetweenWardrobes || items.Count == 0) return;

        if (!ImGui.BeginMenu($"Copy to wardrobe##{id}")) return;
        DrawCopyTargets(items);
        ImGui.EndMenu();
    }

    /// <summary>
    /// The same list behind a button, for the panels that have no menu to hang a submenu off.
    /// </summary>
    /// <remarks>
    /// A popup rather than a menu because <c>BeginMenu</c> needs a menu bar or an open popup above
    /// it, and the selection panel is a plain child window with neither.
    /// </remarks>
    private void DrawCopyToWardrobeButton(IReadOnlyList<WardrobeItem> items, string id)
    {
        if (!CanCopyBetweenWardrobes || items.Count == 0) return;

        var popup = $"##copyto_{id}";

        if (ImGui.Button($" Copy to wardrobe… ##btn_{id}"))
            ImGui.OpenPopup(popup);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Copies the {items.Count} selected item(s) into another wardrobe\n" +
                             "as templates to edit there.");

        if (!ImGui.BeginPopup(popup)) return;
        DrawCopyTargets(items);
        ImGui.EndPopup();
    }

    /// <summary>Every wardrobe but the one in force, as somewhere to copy into.</summary>
    private void DrawCopyTargets(IReadOnlyList<WardrobeItem> items)
    {
        foreach (var profile in _config.Profiles.ToList())
        {
            if (profile.Id == _config.ActiveProfileId) continue;

            // Counted rather than assumed: using the menu twice should not build a wardrobe of
            // duplicates, and saying so up front beats silently doing nothing
            var have = items.Count(i => profile.Items.Any(t => t.CopiedFromId == i.Id));

            var label = have > 0 && have == items.Count
                ? $"{profile.Name}  (already there)"
                : have > 0
                    ? $"{profile.Name}  ({items.Count - have} new)"
                    : profile.Name;

            if (ImGui.MenuItem(label, string.Empty, false, have < items.Count))
            {
                var result  = _profiles.CopyTo(profile, items);
                _copyStatus = result.Skipped > 0
                    ? $"Copied {result.Copied} to '{profile.Name}', skipped {result.Skipped} it already had."
                    : $"Copied {result.Copied} item(s) to '{profile.Name}'.";
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(have == items.Count
                    ? $"'{profile.Name}' already has a copy of everything selected."
                    : $"Copies into '{profile.Name}' as a template.\n\n" +
                      "Same mods and options to start with — switch to that wardrobe\n" +
                      "to change what that character needs. Editing a copy never\n" +
                      "touches the original.");
        }
    }

    /// <summary>The bulk panel's copy block: the button, and what it last did.</summary>
    private void DrawBulkCopyActions()
    {
        if (!CanCopyBetweenWardrobes) return;

        var items = _config.WardrobeItems.Where(i => _selected.Contains(i.Id)).ToList();
        if (items.Count == 0) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Another wardrobe");
        ImGui.Spacing();

        DrawCopyToWardrobeButton(items, "bulk");

        if (!string.IsNullOrEmpty(_copyStatus))
            Hint(_copyStatus);
    }

    /// <summary>The outfit bulk panel's copy block: the selected outfits, sent to another wardrobe.</summary>
    /// <remarks>
    /// The push half of the same job the Import panel does by pulling. Whatever the outfits are
    /// made of goes with them, so what arrives is a look rather than a name.
    /// </remarks>
    private void DrawBulkOutfitCopyActions(IReadOnlyList<Outfit> outfits)
    {
        if (!CanCopyBetweenWardrobes || outfits.Count == 0) return;

        ImGui.TextDisabled("Another wardrobe");
        ImGui.Spacing();

        var popup = "##copyoutfits";

        if (ImGui.Button(" Copy to wardrobe… ##outfits"))
            ImGui.OpenPopup(popup);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Copies the {outfits.Count} selected outfit(s) into another wardrobe," + "\n" +
                             "along with the pieces they are made of.");

        if (ImGui.BeginPopup(popup))
        {
            foreach (var profile in _config.Profiles.ToList())
            {
                if (profile.Id == _config.ActiveProfileId) continue;

                var have = outfits.Count(o => profile.Outfits.Any(t => t.CopiedFromId == o.Id));

                if (ImGui.MenuItem(have == outfits.Count
                                       ? $"{profile.Name}  (already there)"
                                       : profile.Name,
                                   string.Empty, false, have < outfits.Count))
                {
                    var result = _profiles.CopyOutfitsTo(_config.ActiveProfile, profile, outfits,
                                                         out var items);

                    _copyStatus = $"Copied {result.Copied} outfit(s) and {items.Copied} item(s) " +
                                  $"to '{profile.Name}'.";
                }
            }

            ImGui.EndPopup();
        }

        if (!string.IsNullOrEmpty(_copyStatus))
            Hint(_copyStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    // ── Importing from another wardrobe ───────────────────────────────────────

    /// <summary>Whether the pull-from-another-wardrobe panel is showing.</summary>
    private bool _showWardrobeImport;

    private Guid?  _importSourceId;
    private string _importSearch = string.Empty;

    private readonly HashSet<Guid> _importPicked = new();

    /// <summary>Whether the panel is showing the source wardrobe's outfits rather than its items.</summary>
    private bool _importOutfits;

    /// <summary>Opens the panel, on whichever other wardrobe comes first.</summary>
    private void OpenWardrobeImport()
    {
        _showImageBrowser   = false;
        _showTags           = false;
        _showCameraPresets  = false;
        _showWardrobeImport = true;

        _importPicked.Clear();
        _importSearch  = string.Empty;
        _importOutfits = false;

        _importSourceId ??= _config.Profiles.Find(p => p.Id != _config.ActiveProfileId)?.Id;
    }

    /// <summary>
    /// Takes items out of another wardrobe and into this one.
    /// </summary>
    /// <remarks>
    /// The same copy as the card menu's "Copy to wardrobe", pointed the other way. Both directions
    /// earn their place: pushing suits the moment you are looking at a piece and think of who else
    /// should have it, and pulling suits sitting down in an empty wardrobe meaning to furnish it —
    /// which is the one that belongs on the Import menu, beside the other two ways things get in.
    /// <para>
    /// A list rather than a grid of cards. This panel shares the right-hand column with everything
    /// else, and a name and a slot are what you choose by when the pictures are of pieces you
    /// already own.
    /// </para>
    /// </remarks>
    private void DrawWardrobeImportPanel()
    {
        if (DrawPanelHeader("Import From A Wardrobe"))
        {
            _showWardrobeImport = false;
            return;
        }

        var others = _config.Profiles.Where(p => p.Id != _config.ActiveProfileId).ToList();
        if (others.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("There is only one wardrobe.");
            return;
        }

        ImGui.Spacing();

        var source = others.Find(p => p.Id == _importSourceId) ?? others[0];
        _importSourceId = source.Id;

        ImGui.TextDisabled("Take items from");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##importsource", source.Name))
        {
            foreach (var other in others)
            {
                if (!ImGui.Selectable($"{other.Name}  ({other.Items.Count})", other.Id == source.Id))
                    continue;

                _importSourceId = other.Id;
                _importPicked.Clear();
            }
            ImGui.EndCombo();
        }

        Hint($"Into '{_config.ActiveProfile.Name}', the wardrobe you are on.",
             "Copies, not moves — the wardrobe you take from is left exactly as\n" +
             "it is, and editing a copy never touches the original.");

        ImGui.Spacing();

        DrawImportKindToggle("Items",   false, source.Items.Count);
        ImGui.SameLine();
        DrawImportKindToggle("Outfits", true,  source.Outfits.Count);

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##importsearch", "Search…", ref _importSearch, 128);
        ImGui.Spacing();

        if (_importOutfits) DrawWardrobeImportOutfits(source);
        else                DrawWardrobeImportList(source);
    }

    private void DrawImportKindToggle(string label, bool outfits, int count)
    {
        var active = _importOutfits == outfits;

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        if (ImGui.Button($"{label} ({count})##kind") && !active)
        {
            _importOutfits = outfits;
            _importPicked.Clear();
        }

        if (active) ImGui.PopStyleColor(2);
    }

    /// <summary>
    /// The source wardrobe's outfits, which bring their items with them.
    /// </summary>
    /// <remarks>
    /// An outfit is a list of item ids and nothing else, so one copied on its own would arrive as a
    /// name with an empty look behind it. Whatever it is made of comes too — reusing anything
    /// already brought over rather than making a second copy of it.
    /// </remarks>
    private void DrawWardrobeImportOutfits(WardrobeProfile source)
    {
        var target = _config.ActiveProfile;

        var have = new HashSet<Guid>(
            target.Outfits.Where(o => o.CopiedFromId.HasValue).Select(o => o.CopiedFromId!.Value));

        var needle = _importSearch.Trim();
        var outfits = source.Outfits
            .Where(o => needle.Length == 0 ||
                        o.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        o.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(o => o.Name, Services.NaturalOrder.Comparer)
            .ToList();

        if (outfits.Count == 0)
        {
            ImGui.TextDisabled(needle.Length > 0
                ? $"Nothing in '{source.Name}' matches '{needle}'."
                : $"'{source.Name}' has no outfits.");
            return;
        }

        if (ImGui.SmallButton("Select all shown"))
            foreach (var outfit in outfits.Where(o => !have.Contains(o.Id)))
                _importPicked.Add(outfit.Id);

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) _importPicked.Clear();

        ImGui.Spacing();

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginChild("##importoutfits", new Vector2(-1, -footer), true))
        {
            foreach (var outfit in outfits)
            {
                var owned = have.Contains(outfit.Id);

                ImGui.PushID(outfit.Id.ToString());
                if (owned) ImGui.BeginDisabled();

                var picked = _importPicked.Contains(outfit.Id);
                if (ImGui.Checkbox($"{outfit.Name}##pickoutfit", ref picked))
                {
                    if (picked) _importPicked.Add(outfit.Id);
                    else        _importPicked.Remove(outfit.Id);
                }

                if (owned) ImGui.EndDisabled();

                var note = owned ? "already here" : $"{outfit.ItemIds.Count} piece(s)";
                UiLayout.SameLineIfRoomForText(note);
                ImGui.TextDisabled(note);

                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        var chosen = source.Outfits.Where(o => _importPicked.Contains(o.Id)).ToList();
        var canAdd = chosen.Count > 0;

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button($" Import {chosen.Count} outfit(s) ", new Vector2(-1, 0)))
        {
            var result = _profiles.CopyOutfitsTo(source, target, chosen, out var items);
            _importPicked.Clear();

            _copyStatus = items.Copied > 0
                ? $"Imported {result.Copied} outfit(s) and {items.Copied} item(s) they needed."
                : $"Imported {result.Copied} outfit(s); every piece was already here.";
        }
        if (!canAdd) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canAdd
                ? "Brings the outfits and whatever they are made of. Pieces you\n" +
                  "already took across are reused rather than copied again."
                : "Tick some outfits first.");

        if (!string.IsNullOrEmpty(_copyStatus))
            Hint(_copyStatus);
    }

    private void DrawWardrobeImportList(WardrobeProfile source)
    {
        var target = _config.ActiveProfile;

        // Anything already brought over, so a second visit does not offer the same pieces again
        var have = new HashSet<Guid>(
            target.Items.Where(i => i.CopiedFromId.HasValue).Select(i => i.CopiedFromId!.Value));

        var needle = _importSearch.Trim();
        var items = source.Items
            .Where(i => needle.Length == 0 ||
                        i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        i.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name, Services.NaturalOrder.Comparer)
            .ToList();

        var available = items.Where(i => !have.Contains(i.Id)).ToList();

        if (items.Count == 0)
        {
            ImGui.TextDisabled(needle.Length > 0
                ? $"Nothing in '{source.Name}' matches '{needle}'."
                : $"'{source.Name}' is empty.");
            return;
        }

        if (ImGui.SmallButton("Select all shown"))
            foreach (var item in available) _importPicked.Add(item.Id);

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) _importPicked.Clear();

        ImGui.Spacing();

        // Reserve the action row at the bottom, so the list scrolls rather than pushing it off
        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginChild("##importlist", new Vector2(-1, -footer), true))
        {
            foreach (var item in items)
            {
                var owned = have.Contains(item.Id);

                ImGui.PushID(item.Id.ToString());

                if (owned) ImGui.BeginDisabled();

                var picked = _importPicked.Contains(item.Id);
                if (ImGui.Checkbox($"{item.Name}##pick", ref picked))
                {
                    if (picked) _importPicked.Add(item.Id);
                    else        _importPicked.Remove(item.Id);
                }

                if (owned) ImGui.EndDisabled();

                UiLayout.SameLineIfRoomForText(item.Slot.DisplayName());
                ImGui.TextDisabled(owned ? "already here" : item.Slot.DisplayName());

                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        var chosen = source.Items.Where(i => _importPicked.Contains(i.Id)).ToList();
        var canAdd = chosen.Count > 0;

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button($" Import {chosen.Count} item(s) ", new Vector2(-1, 0)))
        {
            var result = _profiles.CopyTo(target, chosen);
            _importPicked.Clear();

            _copyStatus = result.Skipped > 0
                ? $"Imported {result.Copied}, skipped {result.Skipped} already here."
                : $"Imported {result.Copied} item(s) from '{source.Name}'.";
        }
        if (!canAdd) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canAdd
                ? "Copies them in with the same mods and options, ready to be\n" +
                  "edited for this character."
                : "Tick some items first.");

        if (!string.IsNullOrEmpty(_copyStatus))
            Hint(_copyStatus);
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private void DrawProfileSettings()
    {
        Hint("Give each character their own items, outfits and bases.",
             "Tags and styles stay shared across all of them, so a taxonomy is\n" +
             "built once and means the same thing everywhere. Bases, camera\n" +
             "angles and the pictures folder go with the character.");
        ImGui.Spacing();

        var on = _config.PerCharacterWardrobes;
        if (ImGui.Checkbox("Use a separate wardrobe per character", ref on))
        {
            _config.PerCharacterWardrobes = on;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, there is one wardrobe and nothing ever switches — which is\n" +
                             "how the plugin worked before this existed, and what your current\n" +
                             "wardrobe still is.\n\n" +
                             "On, logging in looks for a wardrobe bound to that character and\n" +
                             "asks what to do when there is none. It never switches without\n" +
                             "being told.");

        if (!on)
        {
            Hint("Your wardrobe is untouched either way.",
                 "Turning this on does not divide anything up. It starts as one\n" +
                 "wardrobe holding everything you already have, and characters are\n" +
                 "bound to it — or given their own — one at a time as you log in.");
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Hint("Bind a character to load that wardrobe when you log in as them.",
             "Bind two characters to the same wardrobe and they share it — one set\n" +
             "of items, outfits and bases for both. A character can only be on one\n" +
             "wardrobe, so binding moves them rather than copying them.");
        ImGui.Spacing();

        DrawProfileList();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(UiScale.S(200));
        ImGui.InputTextWithHint("##newprofile", "name a new wardrobe", ref _newProfileName, 64);

        UiLayout.SameLineIfRoomForButton(" Add ");
        var canAdd = !string.IsNullOrWhiteSpace(_newProfileName);
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button(" Add "))
        {
            _config.AddProfile(_newProfileName);
            _newProfileName = string.Empty;
            _config.Save();
        }
        if (!canAdd) ImGui.EndDisabled();
    }

    private void DrawProfileList()
    {
        var current = _profiles.CurrentCharacter;

        foreach (var profile in _config.Profiles.ToList())
        {
            ImGui.PushID(profile.Id.ToString());

            var active = profile.Id == _config.ActiveProfileId;

            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
            }
            if (ImGui.Button(active ? "In use" : "Use", UiScale.S(80, 0)) && !active)
                _profiles.SwitchTo(profile, "picked in settings");
            if (active) ImGui.PopStyleColor(2);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(UiScale.S(180));
            var name = profile.Name;
            if (ImGui.InputText("##name", ref name, 64))
            {
                profile.Name = name;
                _config.Save();
            }

            // Never the last one: everything below reads through a wardrobe, so a config with none
            // is a config with nowhere to put an item
            if (_config.Profiles.Count > 1)
            {
                ImGui.SameLine();
                if (UiLayout.DeleteButton("×",
                        $"Delete '{profile.Name}' and everything in it — " +
                        $"{profile.Items.Count} item(s) and {profile.Outfits.Count} outfit(s).\n\n" +
                        "The mods and the pictures on disk are untouched."))
                {
                    _config.Profiles.Remove(profile);
                    if (active) _config.ActiveProfileId = _config.Profiles[0].Id;
                    _config.Save();

                    ImGui.PopID();
                    continue;
                }
            }

            Hint(profile.Characters.Count > 0
                    ? $"{profile.Items.Count} items, {profile.Outfits.Count} outfits"
                    : $"{profile.Items.Count} items, {profile.Outfits.Count} outfits · not bound to anyone",
                 profile.Characters.Count > 0
                    ? null
                    : "Only ever chosen by hand until a character is bound to it.");

            DrawProfileBinding(profile, current);

            ImGui.Spacing();
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Who this wardrobe loads for, and the button that adds or removes the character you are on.
    /// </summary>
    /// <remarks>
    /// A wardrobe holds a list of characters, not one — so binding a second character to a wardrobe
    /// somebody already uses is how two characters share one. That is the same button doing the
    /// same thing, and it says "Share with" rather than "Bind" when there is already somebody on
    /// the list, because those are two different intentions reaching for it.
    /// <para>
    /// The other direction stays exclusive: binding here first removes the character from every
    /// other wardrobe. A character on two wardrobes would load whichever happened to sit earlier in
    /// the list, which is no answer at all.
    /// </para>
    /// <para>
    /// Only ever the character logged in, rather than a box to type a name and world into. A
    /// binding has to match what the game reports exactly, down to the home world's id, and a typed
    /// name one character out fails silently at the only moment it matters — the next login.
    /// </para>
    /// </remarks>
    private void DrawProfileBinding(WardrobeProfile profile, (string Name, uint World)? current)
    {
        DrawBoundCharacters(profile);

        // Always drawn, disabled when there is nobody to bind. It used to vanish instead, which
        // left the one control that answers "why is this not loading for me" impossible to find.
        var loaded = current is not null;
        var bound  = current is { } c && profile.IsFor(c.Name, c.World);
        var shared = profile.Characters.Count > 0;

        var label = !loaded ? "Bind this character"
                  : bound   ? $"Unbind {current!.Value.Name}"
                  : shared  ? $"Share with {current!.Value.Name}"
                            : $"Bind {current!.Value.Name}";

        if (!loaded) ImGui.BeginDisabled();
        var clicked = ImGui.SmallButton($"{label}##bind");
        if (!loaded) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!loaded
                ? "Nobody is logged in, so there is no character to bind.\n\n" +
                  "Log in and come back — a binding is made from what the game\n" +
                  "reports, never from a name typed in."
                : bound
                    ? $"Stops this wardrobe loading for {current!.Value.Name}.\n\n" +
                      "Nothing in it is removed."
                    : shared
                        ? $"Adds {current!.Value.Name} to this wardrobe, so both they and " +
                          $"{string.Join(" and ", profile.CharacterNames())} load it.\n\n" +
                          "One wardrobe, shared — the same items, outfits and bases for\n" +
                          "each of them. Moves the binding here if another wardrobe had it."
                        : $"Loads this wardrobe whenever you log in as {current!.Value.Name}.");

        if (!clicked || current is not { } who) return;

        if (bound)
        {
            profile.Unbind(who.Name, who.World);
        }
        else
        {
            // A character belongs to one wardrobe, however many characters a wardrobe holds
            foreach (var other in _config.Profiles) other.Unbind(who.Name, who.World);
            profile.Bind(who.Name, who.World);
        }

        _config.Save();
    }

    /// <summary>The characters this wardrobe loads for, each removable.</summary>
    /// <remarks>
    /// Shown per character rather than as one line of names, because a wardrobe shared by three
    /// alts needs a way to drop one of them — and that cannot be done from a login button that only
    /// ever knows about whoever is on screen right now.
    /// </remarks>
    private void DrawBoundCharacters(WardrobeProfile profile)
    {
        if (profile.Characters.Count == 0) return;

        string? remove = null;

        foreach (var key in profile.Characters.ToList())
        {
            var cut  = key.LastIndexOf('@');
            var name = cut > 0 ? key[..cut] : key;

            ImGui.PushID(key);
            ImGui.TextDisabled(name);
            ImGui.SameLine();
            if (UiLayout.DeleteButton("×", $"Stops this wardrobe loading for {name}."))
                remove = key;
            ImGui.PopID();

            UiLayout.SameLineIfRoom(UiScale.S(120f));
        }

        ImGui.NewLine();

        if (remove == null) return;

        profile.Characters.Remove(remove);
        _config.Save();
    }
}
