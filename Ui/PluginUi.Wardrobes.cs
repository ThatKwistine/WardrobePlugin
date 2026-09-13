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

    /// <summary>How much of the batch each other wardrobe already has, and what that was counted for.</summary>
    private readonly Dictionary<Guid, int> _copyTargetHave = new();
    private int _copyTargetStamp;

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
    /// <remarks>
    /// The batch grows to include each item's variants and linked partners before anything is
    /// counted or copied, so "the red dress" arrives as the four colours and the shoes it is worn
    /// with — the same reading the Import panel gives a tick. The label counts what is new after
    /// that, against everything the other wardrobe already holds by any route, not only what this
    /// menu put there.
    /// </remarks>
    private void DrawCopyTargets(IReadOnlyList<WardrobeItem> items)
    {
        var batch = Services.WardrobeProfileService.WithCompanions(_config.ActiveProfile, items);
        var extra = batch.Count - items.Count;

        DrawCopyPicturesSwitch(null);
        ImGui.Separator();

        // Counted rather than assumed: using the menu twice should not build a wardrobe of
        // duplicates, and saying so up front beats silently doing nothing. Counted once per batch
        // rather than per frame, because the count indexes every card of every other wardrobe.
        var stamp = new HashCode();
        stamp.Add(_config.Revision);
        foreach (var item in batch) stamp.Add(item.Id);

        if (_copyTargetStamp != stamp.ToHashCode())
        {
            _copyTargetStamp = stamp.ToHashCode();
            _copyTargetHave.Clear();

            foreach (var profile in _config.Profiles)
            {
                if (profile.Id == _config.ActiveProfileId) continue;
                var present = new Services.WardrobeProfileService.Presence(profile);
                _copyTargetHave[profile.Id] = batch.Count(i => present.Find(i) != null);
            }
        }

        foreach (var profile in _config.Profiles.ToList())
        {
            if (profile.Id == _config.ActiveProfileId) continue;

            var have = _copyTargetHave.TryGetValue(profile.Id, out var h) ? h : 0;

            var label = have > 0 && have == batch.Count
                ? $"{profile.Name}  (already there)"
                : have > 0
                    ? $"{profile.Name}  ({batch.Count - have} new)"
                    : profile.Name;

            if (ImGui.MenuItem(label, string.Empty, false, have < batch.Count))
            {
                var result  = _profiles.CopyTo(profile, batch, PushOptions);
                _copyStatus = result.Skipped > 0
                    ? $"Copied {result.Copied} to '{profile.Name}', skipped {result.Skipped} it already had."
                    : $"Copied {result.Copied} item(s) to '{profile.Name}'.";
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(have == batch.Count
                    ? $"'{profile.Name}' already has everything selected" +
                      (extra > 0 ? " and what goes with it." : ".")
                    : $"Copies into '{profile.Name}' as a template.\n\n" +
                      "Same mods and options to start with — switch to that wardrobe\n" +
                      "to change what that character needs. Editing a copy never\n" +
                      "touches the original." +
                      (extra > 0
                          ? $"\n\nBrings {extra} more: the variants and linked pieces of\n" +
                            "what is selected."
                          : string.Empty));
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
            DrawCopyPicturesSwitch(null);
            ImGui.Separator();

            foreach (var profile in _config.Profiles.ToList())
            {
                if (profile.Id == _config.ActiveProfileId) continue;

                var have = outfits.Count(o => Services.WardrobeProfileService.OutfitAlreadyIn(profile, o));

                if (ImGui.MenuItem(have == outfits.Count
                                       ? $"{profile.Name}  (already there)"
                                       : profile.Name,
                                   string.Empty, false, have < outfits.Count))
                {
                    var result = _profiles.CopyOutfitsTo(_config.ActiveProfile, profile, outfits,
                                                         out var items, PushOptions);

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

    /// <summary>Which slot the item list is narrowed to, or null for all of them.</summary>
    private EquipSlot? _importSlot;

    /// <summary>
    /// What this wardrobe already holds of the source's items, one answer per source item.
    /// </summary>
    /// <remarks>
    /// Rebuilt when either wardrobe changes size or identity rather than every frame: the answer
    /// costs a fingerprint per card, and the list asks for every row. An edit to an item's options
    /// while the panel is open is not seen until the next import, which is the moment it would
    /// have mattered anyway.
    /// </remarks>
    private readonly Dictionary<Guid, WardrobeItem?> _importHeld = new();
    private (Guid Source, Guid Target, int SourceCount, int TargetCount)? _importHeldKey;

    /// <summary>Side of a row's picture: exactly the two lines of text beside it, so a row is one height whatever it holds.</summary>
    private static float ImportThumb => ImGui.GetFrameHeight() + ImGui.GetTextLineHeightWithSpacing();

    /// <summary>Opens the panel, on whichever other wardrobe comes first.</summary>
    private void OpenWardrobeImport()
    {
        _showImageBrowser   = false;
        _showTags           = false;
        _showFolders        = false;
        _showCameraPresets  = false;
        _showWardrobeImport = true;

        _importPicked.Clear();
        _importSearch   = string.Empty;
        _importOutfits  = false;
        _importSlot     = null;
        _importHeldKey  = null;

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
    /// already own — though a small picture beside the name turned out to be what tells two
    /// similarly named cards apart, so the rows carry one unless that is switched off.
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

        // The source falls back when the wardrobe in force changes under an open panel — and the
        // ticks were ids in the wardrobe that just stopped being the source
        if (_importSourceId != source.Id) _importPicked.Clear();
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
                _importSlot = null;
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
        ImGui.InputTextWithHint("##importsearch",
            _importOutfits ? "Search names and tags…" : "Search names, tags, mods…",
            ref _importSearch, 128);

        if (!_importOutfits) DrawImportListControls(source);

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

    /// <summary>The slot filter, the sort order, and the two switches on the item list.</summary>
    /// <remarks>
    /// Dropdowns rather than the grid's row of slot buttons: this column is narrow, and a source
    /// wardrobe can have pieces in twenty slots. Only slots the source actually has are offered,
    /// with counts, so the dropdown doubles as a summary of what is over there.
    /// </remarks>
    private void DrawImportListControls(WardrobeProfile source)
    {
        var slots = source.Items
            .GroupBy(i => i.Slot)
            .OrderBy(g => (int)g.Key)
            .Select(g => (Slot: g.Key, Count: g.Count()))
            .ToList();

        if (_importSlot is { } chosen && slots.All(s => s.Slot != chosen)) _importSlot = null;

        var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;

        ImGui.SetNextItemWidth(half);
        var slotLabel = _importSlot is { } s ? s.DisplayName() : "All slots";
        if (ImGui.BeginCombo("##importslot", slotLabel))
        {
            if (ImGui.Selectable($"All slots  ({source.Items.Count})", _importSlot == null))
                _importSlot = null;

            foreach (var (slot, count) in slots)
            {
                if (ImGui.Selectable($"{slot.DisplayName()}  ({count})", _importSlot == slot))
                    _importSlot = slot;
                if (_importSlot == slot) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(half);
        var order = _config.WardrobeImportOrder;
        if (ImGui.BeginCombo("##importorder", ImportOrderLabel(order)))
        {
            foreach (var option in new[] { WardrobeImportSort.Slot, WardrobeImportSort.Name,
                                           WardrobeImportSort.Newest })
            {
                if (ImGui.Selectable(ImportOrderLabel(option), order == option) && order != option)
                {
                    _config.WardrobeImportOrder = option;
                    _config.Save();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("By slot reads like the grid, with a heading per slot.\n" +
                             "Newest first is for bringing over what the other\n" +
                             "character imported most recently.");

        var pictures = _config.WardrobeImportPictures;
        if (ImGui.Checkbox("Pictures", ref pictures))
        {
            _config.WardrobeImportPictures = pictures;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A small picture beside each row. Off, the list is names and\n" +
                             "slots alone, which is shorter and lighter on a huge wardrobe.");

        DrawImportTagSwitch(source);
    }

    /// <summary>The "tag what arrives" switch, shared by the item and outfit lists.</summary>
    private void DrawImportTagSwitch(WardrobeProfile source)
    {
        var label = $"Tag with '{source.Name}'";

        UiLayout.SameLineIfRoom(UiLayout.CheckboxWidth(label));

        var tag = _config.TagWardrobeImports;
        if (ImGui.Checkbox(label, ref tag))
        {
            _config.TagWardrobeImports = tag;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts the source wardrobe's name on everything this brings in, so\n" +
                             "the copies can be found again once they are mixed in with the\n" +
                             "rest. The originals are not tagged.");

        DrawCopyPicturesSwitch(_config.ActiveProfile);
    }

    /// <summary>
    /// The "copy the pictures too" switch, drawn beside the tag switch in the Import panel and on
    /// its own line in the Copy to wardrobe popups.
    /// </summary>
    /// <remarks>
    /// Disabled, with the reason, when the wardrobe the pictures would go to has no folder of its
    /// own: the setting still holds — it is one setting for both directions — but this particular
    /// copy has nowhere to put them, and a tick that quietly did nothing would be worse than one
    /// that says so.
    /// </remarks>
    private void DrawCopyPicturesSwitch(WardrobeProfile? target)
    {
        const string label = "Copy pictures too";

        if (target != null) UiLayout.SameLineIfRoom(UiLayout.CheckboxWidth(label));

        var folder = target?.ImagesFolder;
        var usable = target == null || (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder));

        if (!usable) ImGui.BeginDisabled();

        var copy = _config.CopyPicturesBetweenWardrobes;
        if (ImGui.Checkbox(label, ref copy))
        {
            _config.CopyPicturesBetweenWardrobes = copy;
            _config.Save();
        }

        if (!usable) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!usable
                ? $"'{target!.Name}' has no pictures folder of its own, so there is nowhere\n" +
                  "to copy them to; the copy will show the same files as the original.\n" +
                  "Give that wardrobe a folder in Settings to turn this on."
                : (target != null
                    ? $"Copies each picture file into '{target.Name}'s own pictures folder, so\n"
                    : "Copies each picture file into the other wardrobe's own pictures folder, so\n") +
                  "the copy has pictures of its own. Off, the copy shows the same files\n" +
                  "as the original — fine until one of them is re-shot, or the other\n" +
                  "wardrobe's folder is tidied.\n\n" +
                  "One setting for both directions, the Import panel and the Copy to\n" +
                  "wardrobe menu. The originals and their files are never touched." +
                  (target == null
                    ? "\nA wardrobe with no pictures folder of its own gets the shared files."
                    : string.Empty));
    }

    private static string ImportOrderLabel(WardrobeImportSort order) => order switch
    {
        WardrobeImportSort.Name   => "By name",
        WardrobeImportSort.Newest => "Newest first",
        _                         => "By slot",
    };

    /// <summary>How the Import panel's copies are made: the tag switch and the pictures switch.</summary>
    private Services.WardrobeProfileService.CopyOptions ImportOptions(WardrobeProfile source) =>
        new(Tag: _config.TagWardrobeImports ? source.Name.Trim() : null,
            Pictures: _config.CopyPicturesBetweenWardrobes);

    /// <summary>How the Copy to wardrobe menu's copies are made: pictures only, no tag.</summary>
    private Services.WardrobeProfileService.CopyOptions PushOptions =>
        new(Pictures: _config.CopyPicturesBetweenWardrobes);

    /// <summary>
    /// What this wardrobe already holds of each of <paramref name="source"/>'s items, cached.
    /// </summary>
    private Dictionary<Guid, WardrobeItem?> ImportHeld(WardrobeProfile source)
    {
        var target = _config.ActiveProfile;
        var key    = (source.Id, target.Id, source.Items.Count, target.Items.Count);

        if (_importHeldKey == key) return _importHeld;

        var present = new Services.WardrobeProfileService.Presence(target);

        _importHeld.Clear();
        foreach (var item in source.Items)
            _importHeld[item.Id] = present.Find(item);

        _importHeldKey = key;
        return _importHeld;
    }

    /// <summary>
    /// The source wardrobe's outfits, which bring their items with them.
    /// </summary>
    /// <remarks>
    /// An outfit is a list of item ids and nothing else, so one copied on its own would arrive as a
    /// name with an empty look behind it. Whatever it is made of comes too — reusing anything
    /// already here rather than making a second copy of it, and the row says how many of its
    /// pieces that is.
    /// </remarks>
    private void DrawWardrobeImportOutfits(WardrobeProfile source)
    {
        var target = _config.ActiveProfile;
        var held   = ImportHeld(source);

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
            foreach (var outfit in outfits.Where(o => !Services.WardrobeProfileService.OutfitAlreadyIn(target, o)))
                _importPicked.Add(outfit.Id);

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) _importPicked.Clear();

        DrawImportTagSwitch(source);

        ImGui.Spacing();

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginChild("##importoutfits", new Vector2(-1, -footer), true))
        {
            foreach (var outfit in outfits)
            {
                var owned = Services.WardrobeProfileService.OutfitAlreadyIn(target, outfit);

                ImGui.PushID(outfit.Id.ToString());
                if (owned) ImGui.BeginDisabled();

                var picked = _importPicked.Contains(outfit.Id);
                if (ImGui.Checkbox($"{outfit.Name}##pickoutfit", ref picked))
                {
                    if (picked) _importPicked.Add(outfit.Id);
                    else        _importPicked.Remove(outfit.Id);
                }

                if (owned) ImGui.EndDisabled();

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    DrawImportOutfitTooltip(source, outfit, held, owned);

                var pieces = outfit.ItemIds.Count(id => source.Items.Exists(i => i.Id == id));
                var here   = outfit.ItemIds.Count(id => held.TryGetValue(id, out var h) && h != null);

                var note = owned          ? "already here"
                         : here == 0      ? $"{pieces} piece(s)"
                         : here == pieces ? $"{pieces} piece(s), all here"
                                          : $"{pieces} piece(s), {here} here";

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
            var result = _profiles.CopyOutfitsTo(source, target, chosen, out var items, ImportOptions(source));
            _importPicked.Clear();
            _importHeldKey = null;

            _copyStatus = items.Copied > 0
                ? $"Imported {result.Copied} outfit(s) and {items.Copied} item(s) they needed."
                : $"Imported {result.Copied} outfit(s); every piece was already here.";
        }
        if (!canAdd) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canAdd
                ? "Brings the outfits and whatever they are made of. Pieces\n" +
                  "already here are reused rather than copied again."
                : "Tick some outfits first.");

        if (!string.IsNullOrEmpty(_copyStatus))
            Hint(_copyStatus);
    }

    /// <summary>What an outfit is made of, and which of those pieces are here already.</summary>
    private void DrawImportOutfitTooltip(WardrobeProfile source, Outfit outfit,
        IReadOnlyDictionary<Guid, WardrobeItem?> held, bool owned)
    {
        ImGui.BeginTooltip();

        if (owned)
        {
            ImGui.TextUnformatted("This wardrobe already has this outfit.");
            ImGui.EndTooltip();
            return;
        }

        var any = false;
        foreach (var id in outfit.ItemIds)
        {
            if (source.Items.Find(i => i.Id == id) is not { } piece) continue;
            any = true;

            var here = held.TryGetValue(id, out var h) && h != null;
            ImGui.TextUnformatted(piece.Name);
            ImGui.SameLine();
            ImGui.TextDisabled(here ? "— here already" : $"— {piece.Slot.DisplayName()}");
        }

        if (outfit.VanillaItems.Count > 0)
        {
            if (any) ImGui.Spacing();
            ImGui.TextDisabled($"{outfit.VanillaItems.Count} vanilla piece(s), which need no copying.");
        }
        else if (!any)
            ImGui.TextDisabled("Made of nothing that still exists over there.");

        ImGui.EndTooltip();
    }

    /// <summary>
    /// The source wardrobe's items, grouped as the grid groups them: originals with their variants
    /// folded beneath.
    /// </summary>
    /// <remarks>
    /// Ticking an original ticks its variants and its linked partners too, which is what somebody
    /// ticking "the red dress" means when the red dress is four colours and a pair of shoes that go
    /// with it. Each can then be unticked on its own. Unticking the original takes its variants
    /// with it and leaves links alone — a linked piece may well be wanted for something else.
    /// <para>
    /// A variant whose original is filtered out of view, or was never in this wardrobe, stands on
    /// its own line: the fold is a reading aid, not a rule about what can be picked.
    /// </para>
    /// </remarks>
    private void DrawWardrobeImportList(WardrobeProfile source)
    {
        var target = _config.ActiveProfile;
        var held   = ImportHeld(source);

        var needle  = _importSearch.Trim();
        var visible = source.Items
            .Where(i => _importSlot == null || i.Slot == _importSlot)
            .Where(i => needle.Length == 0 || ImportSearchMatches(i, needle))
            .ToList();

        if (visible.Count == 0)
        {
            ImGui.TextDisabled(needle.Length > 0 || _importSlot != null
                ? $"Nothing in '{source.Name}' matches."
                : $"'{source.Name}' is empty.");
            return;
        }

        var visibleIds = new HashSet<Guid>(visible.Select(i => i.Id));

        // Originals in the chosen order; a variant sits under its original when both are shown
        var order     = _config.WardrobeImportOrder;
        var originals = Sorted(visible.Where(i => i.VariantOfId is not { } p || !visibleIds.Contains(p)), order)
            .ToList();

        var variantsOf = visible
            .Where(i => i.VariantOfId is { } p && visibleIds.Contains(p))
            .GroupBy(i => i.VariantOfId!.Value)
            .ToDictionary(g => g.Key, g => Sorted(g, order).ToList());

        var available = visible.Where(i => held.TryGetValue(i.Id, out var h) && h == null).ToList();

        if (ImGui.SmallButton("Select all shown"))
            foreach (var item in available) _importPicked.Add(item.Id);

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) _importPicked.Clear();

        if (available.Count < visible.Count)
        {
            var note = $"{visible.Count - available.Count} already here";
            UiLayout.SameLineIfRoomForText(note);
            ImGui.TextDisabled(note);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Greyed out below. A piece is here already when this wardrobe\n" +
                                 "holds a copy of it, the original it was copied from, or the same\n" +
                                 "mod at the same options in the same slot — however it got here.");
        }

        ImGui.Spacing();

        // Reserve the action row at the bottom, so the list scrolls rather than pushing it off
        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginChild("##importlist", new Vector2(-1, -footer), true))
        {
            // Only rows in view are drawn; the rest are skipped as blank space of the same height.
            // A row that is drawn loads its picture, and a wardrobe of five hundred pieces drawn in
            // full every frame was five hundred full-size pictures loaded at once — the stutter
            // that hit whenever the panel opened, and again on every swap of wardrobe while it was.
            var rowHeight = (_config.WardrobeImportPictures ? ImportThumb : ImGui.GetFrameHeight())
                          + ImGui.GetStyle().ItemSpacing.Y;
            var top       = ImGui.GetScrollY() - rowHeight;
            var bottom    = ImGui.GetScrollY() + ImGui.GetWindowHeight() + rowHeight;

            EquipSlot? heading = null;

            foreach (var item in originals)
            {
                if (order == WardrobeImportSort.Slot && _importSlot == null && heading != item.Slot)
                {
                    if (heading != null) ImGui.Spacing();
                    ImGui.TextDisabled(item.Slot.DisplayName());
                    heading = item.Slot;
                }

                var variants = variantsOf.TryGetValue(item.Id, out var v) ? v : null;

                DrawImportRowOrSkip(source, item, held, variants, indent: false, rowHeight, top, bottom);

                if (variants == null) continue;

                ImGui.Indent(UiScale.S(16f));
                foreach (var variant in variants)
                    DrawImportRowOrSkip(source, variant, held, null, indent: true, rowHeight, top, bottom);
                ImGui.Unindent(UiScale.S(16f));
            }
        }
        ImGui.EndChild();

        var chosen = source.Items.Where(i => _importPicked.Contains(i.Id)).ToList();
        var canAdd = chosen.Count > 0;

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button($" Import {chosen.Count} item(s) ", new Vector2(-1, 0)))
        {
            var result = _profiles.CopyTo(target, chosen, ImportOptions(source));
            _importPicked.Clear();
            _importHeldKey = null;

            _copyStatus = result.Skipped > 0
                ? $"Imported {result.Copied}, skipped {result.Skipped} already here."
                : $"Imported {result.Copied} item(s) from '{source.Name}'.";
        }
        if (!canAdd) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var hidden = chosen.Count(i => !visibleIds.Contains(i.Id));
            ImGui.SetTooltip(!canAdd
                ? "Tick some items first."
                : hidden > 0
                    ? "Copies them in with the same mods and options, ready to be\n" +
                      $"edited for this character.\n\n{hidden} of them are ticked but not shown by the\n" +
                      "current search or slot filter — Clear unticks everything."
                    : "Copies them in with the same mods and options, ready to be\n" +
                      "edited for this character.");
        }

        if (!string.IsNullOrEmpty(_copyStatus))
            Hint(_copyStatus);
    }

    private static IEnumerable<WardrobeItem> Sorted(IEnumerable<WardrobeItem> items, WardrobeImportSort order) =>
        order switch
        {
            WardrobeImportSort.Name   => items.OrderBy(i => i.Name, Services.NaturalOrder.Comparer),
            WardrobeImportSort.Newest => items.OrderByDescending(i => i.DateAdded)
                                              .ThenBy(i => i.Name, Services.NaturalOrder.Comparer),
            _                         => items.OrderBy(i => (int)i.Slot)
                                              .ThenBy(i => i.Name, Services.NaturalOrder.Comparer),
        };

    /// <summary>Name, tags, notes, and the mods behind the item, so a piece can be found by any of them.</summary>
    private static bool ImportSearchMatches(WardrobeItem item, string needle) =>
        item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || item.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase))
        || (item.Notes?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.GlamourerItemName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || item.Mods.Any(m => m.ModName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                           || m.ModDirectory.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>The row if any of it is in view, otherwise the space it would take.</summary>
    /// <remarks>
    /// The blank is the row's nominal height, and a drawn row is padded to the same, so the
    /// scroll range does not shift as rows come into view. Text-only rows are a frame high exactly;
    /// picture rows are the picture plus spacing.
    /// </remarks>
    private void DrawImportRowOrSkip(WardrobeProfile source, WardrobeItem item,
        IReadOnlyDictionary<Guid, WardrobeItem?> held, IReadOnlyList<WardrobeItem>? variants, bool indent,
        float rowHeight, float top, float bottom)
    {
        var y = ImGui.GetCursorPosY();

        if (y + rowHeight < top || y > bottom)
        {
            ImGui.Dummy(new Vector2(0f, rowHeight - ImGui.GetStyle().ItemSpacing.Y));
            return;
        }

        DrawImportRow(source, item, held, variants, indent);
    }

    /// <summary>One row of the item list: picture, tick box, and what the piece is.</summary>
    /// <param name="variants">The variants folded under this row, or null for a row that has none.</param>
    private void DrawImportRow(WardrobeProfile source, WardrobeItem item,
        IReadOnlyDictionary<Guid, WardrobeItem?> held, IReadOnlyList<WardrobeItem>? variants, bool indent)
    {
        var owned    = held.TryGetValue(item.Id, out var existing) ? existing : null;
        var pictures = _config.WardrobeImportPictures;
        var thumb    = pictures ? ImportThumb : 0f;

        ImGui.PushID(item.Id.ToString());

        if (pictures)
        {
            if (ItemTexture(item)?.GetWrapOrDefault() is { } wrap)
                ImageDraw.Square(wrap, thumb);
            else
            {
                var at = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddRectFilled(at, at + new Vector2(thumb, thumb),
                    ImGui.GetColorU32(new Vector4(0.07f, 0.07f, 0.09f, 1f)));
                ImGui.Dummy(new Vector2(thumb, thumb));
            }

            if (ImGui.IsItemHovered()) DrawImportPreview(item);

            ImGui.SameLine();
            ImGui.BeginGroup();
        }

        if (owned != null) ImGui.BeginDisabled();

        var picked = _importPicked.Contains(item.Id);
        if (ImGui.Checkbox($"{item.Name}##pick", ref picked))
        {
            if (picked)
            {
                _importPicked.Add(item.Id);

                foreach (var companion in Services.WardrobeProfileService.WithCompanions(source, new[] { item }))
                    if (held.TryGetValue(companion.Id, out var h) && h == null)
                        _importPicked.Add(companion.Id);
            }
            else
            {
                _importPicked.Remove(item.Id);

                if (variants != null)
                    foreach (var variant in variants)
                        _importPicked.Remove(variant.Id);
            }
        }

        if (owned != null) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            DrawImportRowTooltip(source, item, owned, variants);

        // The second line, or the tail of the only line: slot, then what travels with it
        var parts = new List<string>();

        if (owned != null)
        {
            parts.Add(owned.CopiedFromId == item.Id || item.CopiedFromId == owned.Id
                ? "already here"
                : owned.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)
                    ? "already here"
                    : $"here as '{owned.Name}'");
        }
        else
        {
            if (!indent) parts.Add(item.Slot.DisplayName());

            var links = item.LinkedItemIds.Count(id => source.Items.Exists(i => i.Id == id));
            if (variants is { Count: > 0 }) parts.Add($"+{variants.Count} variant(s)");
            if (links > 0)                  parts.Add($"+{links} linked");
        }

        var note = string.Join(" · ", parts);

        if (pictures)
        {
            if (note.Length > 0) ImGui.TextDisabled(note);
            ImGui.EndGroup();

            // Rows are as tall as the picture whatever the text does, so the list reads as rows
            var shortfall = thumb - ImGui.GetItemRectSize().Y;
            if (shortfall > 0f) ImGui.Dummy(new Vector2(0f, shortfall - ImGui.GetStyle().ItemSpacing.Y));
        }
        else if (note.Length > 0)
        {
            UiLayout.SameLineIfRoomForText(note);
            ImGui.TextDisabled(note);
        }

        ImGui.PopID();
    }

    /// <summary>The picture at a size you can actually see, on hover.</summary>
    private void DrawImportPreview(WardrobeItem item)
    {
        if (ItemTexture(item)?.GetWrapOrDefault() is not { } wrap) return;

        ImGui.BeginTooltip();
        ImageDraw.Square(wrap, UiScale.S(220f));
        ImGui.EndTooltip();
    }

    /// <summary>Why a row is greyed out, or what ticking it brings.</summary>
    private void DrawImportRowTooltip(WardrobeProfile source, WardrobeItem item, WardrobeItem? owned,
        IReadOnlyList<WardrobeItem>? variants)
    {
        ImGui.BeginTooltip();

        if (owned != null)
        {
            var how = owned.CopiedFromId == item.Id ? "a copy of this"
                    : item.CopiedFromId == owned.Id ? "the original this was copied from"
                    : owned.CopiedFromId != null && owned.CopiedFromId == item.CopiedFromId
                                                    ? "a copy of the same original"
                                                    : "the same mod at the same options, in the same slot";

            ImGui.TextUnformatted($"This wardrobe already has this piece: '{owned.Name}' is {how}.");
        }
        else
        {
            var mods = item.Mods.Where(m => !string.IsNullOrEmpty(m.ModName)).Select(m => m.ModName).ToList();
            if (mods.Count > 0)
                ImGui.TextDisabled(string.Join(", ", mods));
            else if (!string.IsNullOrEmpty(item.GlamourerItemName))
                ImGui.TextDisabled(item.GlamourerItemName);

            var links = item.LinkedItemIds
                .Select(id => source.Items.Find(i => i.Id == id))
                .Where(i => i != null)
                .Select(i => i!.Name)
                .ToList();

            if (variants is { Count: > 0 } || links.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("Ticking this also ticks:");
                if (variants is { Count: > 0 })
                    ImGui.TextDisabled($"  {variants.Count} variant(s) — untick any you do not want");
                foreach (var name in links)
                    ImGui.TextDisabled($"  {name}, which is linked to it");
            }
        }

        ImGui.EndTooltip();
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
                    Configuration.DeleteLastWornFile(profile);
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
