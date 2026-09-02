using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using WardrobePlugin.Models;

namespace WardrobePlugin.Ui;

/// <summary>
/// The toolbar the menu bar replaced, kept for anyone who preferred it.
/// </summary>
/// <remarks>
/// Two rows of buttons above the grid: where items come from, then what the character is wearing,
/// with the item count and the card-size control at the right-hand ends. Off by default — the menu
/// bar is what a new install gets — and the whole of this file draws nothing until it is turned on.
/// <para>
/// Kept because the two are a genuine trade rather than an improvement. A button is one press and
/// its state is visible without opening anything; a menu is two presses and gives the row back to
/// the grid. People who had learned where fifteen buttons were should not have to relearn them
/// because the default changed.
/// </para>
/// <para>
/// Settings are the exception and are not offered in the old shape. The nineteen-section scroll
/// was not a trade — it was one column holding every setting the plugin has, with the grouping no
/// longer describing anything — so the button here opens the same window the menu does.
/// </para>
/// </remarks>
public partial class PluginUi
{
    /// <summary>The choice between the menu bar and the old toolbar.</summary>
    private void DrawToolbarSettings()
    {
        Hint("What sits above the grid.",
             "The menu bar groups the same actions into four menus and gives the" + "\n" +
             "row back to your items. The old toolbar keeps every action as its" + "\n" +
             "own button, visible and one press away.");
        ImGui.Spacing();

        var classic = _config.ClassicToolbar;
        if (ImGui.Checkbox("Use the old button toolbar", ref classic))
        {
            _config.ClassicToolbar = classic;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Two rows of buttons instead of the menu bar, as the wardrobe looked" + "\n" +
                             "before 1.6." + "\n" + "\n" +
                             "Everything is reachable either way. Settings open in their own" + "\n" +
                             "window whichever you choose.");

        Hint(classic
                ? "Two rows of buttons above the grid."
                : "Four menus: Import, Character, View and Screenshots.",
             classic
                ? "Card size is the magnifier at the right-hand end of the second row," + "\n" +
                  "and the item count sits above it."
                : "Card size is under View, and the item count is at the right-hand" + "\n" +
                  "end of the bar.");
    }

    /// <summary>
    /// The Settings button, which opens the window rather than a panel.
    /// </summary>
    /// <remarks>
    /// Lit while the window is open, so it still reads as the toggle it used to be. It cannot go
    /// through <c>ToggleButton</c> like the panels beside it, because that takes the flag by
    /// reference and this one is a property standing in for the window's own open state.
    /// </remarks>
    private void DrawClassicSettingsButton()
    {
        var open = _showSettings;

        if (open)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.3f, 0.5f, 0.8f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
        }

        if (ImGui.Button(" Settings ")) _showSettings = !open;

        if (open) ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Everything the wardrobe can be told to do differently." + "\n" + "\n" +
                             "Opens in its own window, so it can sit beside the grid while" + "\n" +
                             "you change something and watch what it does.");
    }

    /// <summary>
    /// Which wardrobe is in force, and the way into another one.
    /// </summary>
    /// <remarks>
    /// The menu bar carries this under Character; the old toolbar predates per-character wardrobes
    /// entirely, so it needs somewhere of its own. A button and a popup rather than more buttons on
    /// a row that already has fifteen — and drawn only when the feature is on, so a toolbar that
    /// never wanted it never grows.
    /// </remarks>
    private void DrawClassicWardrobeButton()
    {
        if (!_config.PerCharacterWardrobes) return;

        const string popup = "##classicwardrobes";
        var label = $" {Shorten(_config.ActiveProfile.Name)} ";

        UiLayout.SameLineIfRoomForButton(label);

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.42f, 0.3f, 0.62f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.52f, 0.38f, 0.74f, 1f));
        if (ImGui.Button(label)) ImGui.OpenPopup(popup);
        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The wardrobe everything here is acting on." + "\n" + "\n" +
                             "Click to switch to another, or to take items out of one.");

        if (!ImGui.BeginPopup(popup)) return;

        foreach (var profile in _config.Profiles.ToList())
        {
            var active = profile.Id == _config.ActiveProfileId;

            if (ImGui.MenuItem(profile.Name, $"{profile.Items.Count} items", active) && !active)
                _profiles.SwitchTo(profile, "picked from the toolbar");
        }

        if (CanCopyBetweenWardrobes)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Import from another wardrobe…")) OpenWardrobeImport();
        }

        ImGui.EndPopup();
    }

    private void DrawClassicToolbar()
    {
        // Row 1: import + panels
        if (ImGui.Button("  + Import from Mod  "))
            _panel.OpenImport();

        UiLayout.SameLineIfRoomForButton("  Mass Import  ");
        if (ImGui.Button("  Mass Import  "))
            _massImport.Open();

        UiLayout.SameLineIfRoomForButton("  Share  ");
        if (ImGui.Button("  Share  "))
            _share.Open();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Send your wardrobe to somebody as a single file, or open\n" +
                             "one they sent you and take pieces from it.\n\n" +
                             "A share file describes items — which mod, which options —\n" +
                             "and never contains the mods themselves, so both sides need\n" +
                             "to own a mod for an item to work.");

        UiLayout.SameLineIfRoomForButton(" Select ");
        var wasSelecting = _selectMode;
        ToggleButton(" Select ", ref _selectMode);
        // ToggleButton only reports activation, and leaving the mode has to drop the selection
        if (wasSelecting && !_selectMode) ExitSelectMode();
        if (!wasSelecting && _selectMode) EnterSelectMode();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick several cards and act on them together.\n" +
                             "Cards show a tick box instead of their buttons while this is on.\n\n" +
                             "Works on whichever grid you are looking at — items, or outfits\n" +
                             "and design cards.");

        UiLayout.SameLineIfRoomForButton(" Crop Guide ");
        DrawCropGuideToggle();

        UiLayout.SameLineIfRoomForButton("  Images  ");
        ToggleButton("  Images  ", ref _showImageBrowser, onActivate: () =>
        {
            _showSettings = false;
            _showTags     = false;
            RefreshBrowserImages();
        });

        UiLayout.SameLineIfRoomForButton(" Settings ");
        DrawClassicSettingsButton();

        UiLayout.SameLineIfRoomForButton(" Tags ");
        ToggleButton(" Tags ", ref _showTags, onActivate: () =>
        {
            _showImageBrowser = false;
            _showSettings     = false;

            // Whatever the last visit ended on has been read by now, and would otherwise reappear
            // as if something had just happened
            _newTag       = string.Empty;
            _newTagStatus = string.Empty;
        });


        if (_session.CanStart)
        {
            UiLayout.SameLineIfRoomForButton(" Screenshot Session ");
            if (ImGui.Button(" Screenshot Session "))
            {
                _showImageBrowser = false;
                _showSettings     = false;

                // Explicitly not the automatic kind. The two buttons are what say which sort of run
                // this is, so pressing this one has to mean it even when the last was the other.
                _session.Auto = false;
                _session.Start();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Automatically wear each unimaged item and watch for a\n" +
                                 "new screenshot, then crop it to 1:1 and assign it.\n\n" +
                                 "You take the screenshots." +
                                 (_session.AutoEnabled
                                     ? "\n\nSuper Screenshot Session beside this takes them for you."
                                     : "\n\nSettings → Experimental can have the wardrobe take them\n" +
                                       "for you instead."));

            DrawSuperSessionButton();
        }

        DrawClassicWardrobeButton();

        DrawItemCount();

        // Row 2: wardrobe actions
        ImGui.Spacing();

        if (_config.WornItems.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.45f, 0.08f, 0.08f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.12f, 0.12f, 1f));
            if (ImGui.Button(" Unequip All "))
            {
                // Ctrl takes the base off too. Without it this leaves the base character on, the same
                // as Strip does — the difference between the two buttons is what the emptied slots are
                // set to, not whether the base survives.
                var unequipAnims = ImGui.GetIO().KeyShift;
                _wardrobe.StripAll(ignoreBase: ImGui.GetIO().KeyCtrl, toNothing: true,
                    stripAnimations: unequipAnims);

                ForgetWorn(unequipAnims);
                _scanStatus = string.Empty;
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Take off every wardrobe item and empty every slot in\n" +
                                 "Glamourer — set to Nothing, not to an invisible item, so\n" +
                                 "the character is wearing nothing at all.\n\n" +
                                 "Animations, VFX and mounts are left running — they are not\n" +
                                 "on the character, so there is nothing to unequip.\n\n" +
                             "Hold Shift to take those off as well." +
                                 (_config.ActiveBaseCharacter is { } unequipBase
                                     ? $"\n\nKeeps '{unequipBase.Name}': its slots and items stay on.\n" +
                                       "Hold Ctrl to take that off as well, leaving nothing at all."
                                     : string.Empty));
            UiLayout.SameLineIfRoomForButton(" Strip ");
        }

        // Strip: force Emperor's New on every slot regardless of tracking
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.35f, 0.06f, 0.06f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.10f, 0.10f, 1f));
        if (ImGui.Button(" Strip "))
        {
            var stripAnims = ImGui.GetIO().KeyShift;
            _wardrobe.StripAll(stripAnimations: stripAnims);

            ForgetWorn(stripAnims);
            _scanStatus = string.Empty;
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force every equipment slot to Emperor's New in Glamourer\n" +
                             "and disable all worn mods.\n\n" +
                             "Animations, VFX and mounts are left running — they are not\n" +
                             "on the character, so there is nothing to strip.\n\n" +
                             "Hold Shift to take those off as well." +
                             (_config.ActiveBaseCharacter is { } stripBase
                                 ? $"\n\nStrips down to '{stripBase.Name}': its slots and items stay on."
                                 : string.Empty));

        UiLayout.SameLineIfRoomForButton(" In-Game Look ");

        // The counterpart to Strip: that one empties every slot, this one gets out of the way so the
        // gear the game has on shows through. Blue rather than the reds beside it — nothing is being
        // taken away here that the server was not already showing everyone else.
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
        if (ImGui.Button(" In-Game Look "))
        {
            // Ctrl leaves the base off, as it does on Unequip All. The setting beside this button in
            // a plate's panel is the standing answer; this is the one-off, for the moment you want to
            // see the character with nothing of the wardrobe's on them at all.
            var ignoreBase = ImGui.GetIO().KeyCtrl;
            var revertAnims = ImGui.GetIO().KeyShift;
            var removed     = _wardrobe.RevertToInGameLook(ignoreBase, revertAnims);

            ForgetWorn(revertAnims);

            var without = ignoreBase && _config.ActiveBaseCharacter is { } dropped
                ? $" '{dropped.Name}' left off."
                : string.Empty;

            _scanStatus = removed > 0
                ? $"Took off {removed} item(s) — showing the game's own look.{without}"
                : $"Already showing the game's own look.{without}";
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Take the wardrobe's clothes off and clear Glamourer, so the\n" +
                             "character shows exactly what the game has on them —\n" +
                             "including any glamour plate you have applied.\n\n" +
                             "Animations, VFX and mounts are left running, as with Strip —\n" +
                             "hold Shift to take those off as well." +
                             (_config.KeepBaseCharacterOnRevert && _config.ActiveBaseCharacter is { } revertBase
                                 ? $"\n\n'{revertBase.Name}' goes back on top. Hold Ctrl to leave it\n" +
                                   "off for this press, or turn it off for good beside Show\n" +
                                   "In-Game Look in any glamour plate's edit panel."
                                 : _config.ActiveBaseCharacter != null
                                     ? "\n\nYour base character is not held back — the revert is\n" +
                                       "absolute, showing the unmodded character."
                                     : string.Empty));

        UiLayout.SameLineIfRoomForButton(" Refresh ");

        // Refresh: Penumbra redraw local player
        if (ImGui.Button(" Refresh "))
            Plugin.Penumbra.RedrawPlayer();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tell Penumbra to redraw the local player character.");

        UiLayout.SameLineIfRoomForButton(" Scan ");

        // Scan: detect worn state from enabled Penumbra mods
        if (ImGui.Button(" Scan "))
        {
            _detectedWorn.Clear();
            var scan = _wardrobe.ScanAndSyncWorn();
            foreach (var id in scan.Adopted) _detectedWorn.Add(id);

            // Also mark items already in WornItems as detected
            foreach (var id in _config.WornItems.Values)
                _detectedWorn.Add(id);

            _desynced = new List<WardrobeItem>(scan.Desynced);

            _scanStatus = scan.Adopted.Count > 0
                ? $"Detected {scan.Adopted.Count} new item(s) as worn."
                : _detectedWorn.Count > 0
                    ? "Wardrobe already in sync."
                    : "No wardrobe items detected as worn.";
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan Penumbra for enabled mods and mark matching\n" +
                             "wardrobe items as worn, and report any whose mods are\n" +
                             "enabled without Glamourer showing them.");

        if (!string.IsNullOrEmpty(_scanStatus))
        {
            UiLayout.SameLineIfRoomForText(_scanStatus);
            ImGui.TextDisabled(_scanStatus);
        }

        // Last, so it takes the right-hand end of the row rather than a place in the queue
        DrawCardSizeButton();
    }

    /// <summary>
    /// Wardrobe item count, right-aligned on the toolbar's first row. Shows how many of the total
    /// are currently visible whenever a filter or search is narrowing the grid.
    /// </summary>
    private void DrawItemCount()
    {
        var total = _config.WardrobeItems.Count;
        if (total == 0) return;

        var text = _visibleCount == total
            ? $"{total} item{(total == 1 ? "" : "s")}"
            : $"{_visibleCount} of {total} items";

        ImGui.SameLine();
        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

        ImGui.AlignTextToFramePadding();

        // Placed to sit exactly against the right edge, so wrapping there would break it onto a
        // second line for a rounding error's worth of width
        UiLayout.PushNoWrap();
        ImGui.TextDisabled(text);
        UiLayout.PopWrap();

        if (_visibleCount != total && ImGui.IsItemHovered())
            ImGui.SetTooltip("Filtered by the current search, slot, style, tag, worn or favourites " +
                             "selection.");
    }

    /// <summary>
    /// The card size slider, behind an icon under the item count.
    /// </summary>
    /// <remarks>
    /// The same setting as the one in Settings, put where it is used. Card size is judged by looking
    /// at the grid and dragging until it looks right, and a slider four panels away from the thing
    /// it resizes cannot be judged at all — you set it, close Settings, look, and go back.
    /// </remarks>
    /// <summary>Popup id for the card-size sliders. See <see cref="DrawCardSizeButton"/>.</summary>
    private const string CardSizePopup = "##cardsizeslider";

    private void DrawCardSizeButton()
    {
        var text = FontAwesomeIcon.Search.ToIconString();

        float width;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            width = ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2;

        // Against the right edge of the actions row. Placed rather than queued: it is a view control
        // among wardrobe actions, so it belongs at the far end rather than in the run of buttons that
        // change what is worn.
        ImGui.SameLine();

        var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width;
        if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            clicked = ImGui.Button($"{text}##cardsizepop");

        if (clicked) ImGui.OpenPopup(CardSizePopup);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Card size — items {_config.CardScale:0.00}x, " +
                             $"outfits {_config.OutfitCardScale:0.00}x\nClick to change them.");

        if (!ImGui.BeginPopup(CardSizePopup)) return;

        ImGui.TextDisabled("Card size");
        ImGui.Spacing();

        // Both grids, since the popup is reachable from either and the two are set against each other
        DrawCardScaleSlider("Items", UiScale.S(220));
        ImGui.Spacing();
        DrawCardScaleSlider("Outfits", UiScale.S(220));

        ImGui.EndPopup();
    }

    /// <summary>
    /// Starts a session that photographs the whole wardrobe by itself.
    /// </summary>
    /// <remarks>
    /// Beside the ordinary one rather than folded into it, because the difference between them is the
    /// whole feature: one waits on you for every picture and the other waits on nobody, and which of
    /// those is about to happen should be the button you pressed rather than the state of a tick box
    /// in a panel behind the character.
    /// <para>
    /// Only when the feature is turned on in Experimental, so the toolbar of anyone who has not asked
    /// for it looks exactly as it did.
    /// </para>
    /// </remarks>
    private void DrawSuperSessionButton()
    {
        if (!_session.AutoEnabled) return;

        const string label = " Super Screenshot Session ";
        UiLayout.SameLineIfRoomForButton(label);

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.18f, 0.32f, 0.5f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.44f, 0.68f, 1f));
        var pressed = ImGui.Button(label);
        ImGui.PopStyleColor(2);

        if (pressed)
        {
            _showImageBrowser = false;
            _showSettings     = false;
            _session.StartSuper();
        }

        if (!ImGui.IsItemHovered()) return;

        // The two things that make a run come out wrong are both knowable before it starts, so they
        // are said here rather than found out three hundred pictures later
        var warning = !Plugin.Camera.InGpose
            ? "\n\nYou are not in GPose — camera angles will not be applied and every\n" +
              "picture will be taken from wherever the camera is standing."
            : string.Empty;

        ImGui.SetTooltip("Photograph every item without a picture, start to finish: each one is\n" +
                         "worn in turn, the camera moves to the angle its slot asks for, the shot\n" +
                         "is taken, cropped and filed, and it moves on. Nothing to press.\n\n" +
                         "Set your camera angles up first, and start it in GPose. Everything it\n" +
                         "does is written to the log — open it with /xllog.\n\n" +
                         "Experimental. Watch the first few shots before leaving it to run." +
                         warning);
    }
}
