using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Models;

namespace WardrobePlugin.Ui;

/// <summary>
/// The window's menu bar — what used to be two rows of fifteen buttons above the grid.
/// </summary>
/// <remarks>
/// Every one of those buttons was the same size and the same colour, so nothing on the row said
/// which of them you press constantly and which you press once a month. Four menus say it by
/// grouping: where items come from, what the character is wearing, what the window is showing, and
/// photographing it.
/// <para>
/// The toggles keep their lit-up state as a menu tick, which is the one thing a menu would
/// otherwise lose against a highlighted button, and the entries that behaved differently with Ctrl
/// held say so in the shortcut column rather than only in a tooltip nobody hovers for.
/// </para>
/// </remarks>
public partial class PluginUi
{
    // The colour language from the buttons these replaced: red takes the character's clothes off,
    // blue puts the game's own look back. Text rather than button colours — a menu entry has no
    // frame to fill, so the word itself has to carry it.
    private static readonly Vector4 MenuStrip  = new(1f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 MenuRevert = new(0.55f, 0.78f, 1f, 1f);

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar()) return;

        DrawImportMenu();
        DrawCharacterMenu();
        DrawViewMenu();
        DrawScreenshotsMenu();
        DrawMenuBarStatus();

        ImGui.EndMenuBar();
    }

    // ── Menus ─────────────────────────────────────────────────────────────────

    private void DrawImportMenu()
    {
        if (!ImGui.BeginMenu("Import")) return;

        if (MenuAction("From a Mod…", "Build one item from one mod, choosing its options as you go."))
            _panel.OpenImport();

        if (MenuAction("Mass Import…", "Work through many mods in a row, one screen each."))
            _massImport.Open();

        if (CanCopyBetweenWardrobes &&
            MenuAction("From Another Wardrobe…",
                "Take items out of one of your other wardrobes and into\n" +
                "this one, as copies to edit for this character."))
            OpenWardrobeImport();

        ImGui.Separator();

        if (MenuAction("Share…",
                "Send your wardrobe to somebody as a single file, or open\n" +
                "one they sent you and take pieces from it.\n\n" +
                "A share file describes items — which mod, which options —\n" +
                "and never contains the mods themselves, so both sides need\n" +
                "to own a mod for an item to work."))
            _share.Open();

        ImGui.EndMenu();
    }

    private void DrawCharacterMenu()
    {
        if (!ImGui.BeginMenu("Character")) return;

        // First, and only when there is more than one: which wardrobe is in force decides what
        // every entry under it acts on
        DrawProfileMenu();

        DrawUnequipAllItem();
        DrawStripItem();
        DrawInGameLookItem();

        ImGui.Separator();

        if (MenuAction("Refresh", "Tell Penumbra to redraw the local player character."))
            Plugin.Penumbra.RedrawPlayer();

        if (MenuAction("Scan",
                "Scan Penumbra for enabled mods and mark matching\n" +
                "wardrobe items as worn, and report any whose mods are\n" +
                "enabled without Glamourer showing them."))
            RunWornScan();

        ImGui.EndMenu();
    }

    private void DrawViewMenu()
    {
        if (!ImGui.BeginMenu("View")) return;

        MenuPanelToggle("Images", ref _showImageBrowser,
            "Every picture the wardrobe holds, in one list.",
            onActivate: () =>
            {
                _showTags = false;
                RefreshBrowserImages();
            });

        MenuPanelToggle("Tags", ref _showTags,
            "The tag tree — filter the grid by tag, and rename or recolour them.",
            onActivate: () =>
            {
                _showImageBrowser = false;

                // Whatever the last visit ended on has been read by now, and would otherwise
                // reappear as if something had just happened
                _newTag       = string.Empty;
                _newTagStatus = string.Empty;
            });

        DrawSettingsWindowItem();

        ImGui.Separator();

        DrawSelectModeItem();
        DrawCropGuideMenu();

        ImGui.Separator();

        DrawCardSizeMenu();

        ImGui.EndMenu();
    }

    /// <remarks>
    /// Named for the subject rather than for the one feature that started it: camera angles are not
    /// a session, and neither is anything else that will end up here.
    /// </remarks>
    private void DrawScreenshotsMenu()
    {
        if (!ImGui.BeginMenu("Screenshots")) return;

        DrawScreenshotSessionItem();
        DrawSuperSessionItem();

        ImGui.Separator();

        if (MenuAction("Camera Angles…",
                "The angle the camera moves to for each slot, and the\n" +
                "presets saved against them."))
        {
            _showImageBrowser  = false;
            _showTags          = false;
            _showCameraPresets = true;
        }

        ImGui.EndMenu();
    }

    // ── Character entries ─────────────────────────────────────────────────────

    private void DrawUnequipAllItem()
    {
        var worn = _config.WornItems.Count > 0;

        ImGui.PushStyleColor(ImGuiCol.Text, MenuStrip);
        var clicked = ImGui.MenuItem("Unequip All", StripHint(_config.ActiveBaseCharacter != null),
            false, worn);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(worn
                ? "Take off every wardrobe item and empty every slot in\n" +
                  "Glamourer — set to Nothing, not to an invisible item, so\n" +
                  "the character is wearing nothing at all.\n\n" +
                  "Animations, VFX and mounts are left running — they are not\n" +
                  "on the character, so there is nothing to unequip.\n\n" +
                  "Hold Shift to take those off as well." +
                  (_config.ActiveBaseCharacter is { } unequipBase
                      ? $"\n\nKeeps '{unequipBase.Name}': its slots and items stay on.\n" +
                        "Hold Ctrl to take that off as well, leaving nothing at all."
                      : string.Empty)
                : "Nothing of the wardrobe's is on. Strip below empties every\n" +
                  "slot whether or not the wardrobe is tracking it.");

        if (!clicked) return;

        // Ctrl takes the base off too. Without it this leaves the base character on, the same
        // as Strip does — the difference between the two entries is what the emptied slots are
        // set to, not whether the base survives.
        var animations = ImGui.GetIO().KeyShift;
        _wardrobe.StripAll(ignoreBase: ImGui.GetIO().KeyCtrl, toNothing: true,
            stripAnimations: animations);

        ForgetWorn(animations);
        _scanStatus = string.Empty;
    }

    private void DrawStripItem()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MenuStrip);
        var clicked = ImGui.MenuItem("Strip", StripHint(false));
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force every equipment slot to Emperor's New in Glamourer\n" +
                             "and disable all worn mods.\n\n" +
                             "Animations, VFX and mounts are left running — they are not\n" +
                             "on the character, so there is nothing to strip.\n\n" +
                             "Hold Shift to take those off as well." +
                             (_config.ActiveBaseCharacter is { } stripBase
                                 ? $"\n\nStrips down to '{stripBase.Name}': its slots and items stay on."
                                 : string.Empty));

        if (!clicked) return;

        var animations = ImGui.GetIO().KeyShift;
        _wardrobe.StripAll(stripAnimations: animations);

        ForgetWorn(animations);
        _scanStatus = string.Empty;
    }

    private void DrawInGameLookItem()
    {
        // Only when the base would otherwise come back on. With KeepBaseCharacterOnRevert off the
        // revert is already absolute, so there is nothing for Ctrl to do and saying otherwise is a
        // promise the key does not keep.
        var keepsBase = _config.KeepBaseCharacterOnRevert && _config.ActiveBaseCharacter != null;

        ImGui.PushStyleColor(ImGuiCol.Text, MenuRevert);
        var clicked = ImGui.MenuItem("Show In-Game Look", StripHint(keepsBase));
        ImGui.PopStyleColor();

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

        if (!clicked) return;

        // Ctrl leaves the base off, as it does on Unequip All. The setting in a plate's panel is
        // the standing answer; this is the one-off, for the moment you want to see the character
        // with nothing of the wardrobe's on them at all.
        var ignoreBase = ImGui.GetIO().KeyCtrl;
        var animations = ImGui.GetIO().KeyShift;
        var removed    = _wardrobe.RevertToInGameLook(ignoreBase, animations);

        ForgetWorn(animations);

        var without = ignoreBase && _config.ActiveBaseCharacter is { } dropped
            ? $" '{dropped.Name}' left off."
            : string.Empty;

        _scanStatus = removed > 0
            ? $"Took off {removed} item(s) — showing the game's own look.{without}"
            : $"Already showing the game's own look.{without}";
    }

    /// <summary>
    /// Drops the worn markers for everything except the mod categories.
    /// </summary>
    /// <remarks>
    /// An animation or a mount is not on the character, so nothing that empties equipment slots
    /// turned it off — and the grid must not claim otherwise. Shared by all three entries above,
    /// which had the same lines copied between them.
    /// <para>
    /// Unless Shift was held, in which case they were taken off with everything else and their
    /// markers go too.
    /// </para>
    /// </remarks>
    private void ForgetWorn(bool includeAnimations) =>
        _detectedWorn.RemoveWhere(id =>
            _config.WardrobeItems.Find(x => x.Id == id) is not { } item
            || includeAnimations
            || !item.Slot.IsModCategory());

    private void RunWornScan()
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

    // ── View entries ──────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the settings window, ticked while it is on screen.
    /// </summary>
    /// <remarks>
    /// Not a panel toggle like the two above it any more: settings are a window of their own, so
    /// opening them takes nothing away from the grid and closes none of the other panels. Dalamud's
    /// settings button on the plugin installer opens the same window.
    /// </remarks>
    private void DrawSettingsWindowItem()
    {
        var open = _showSettings;

        if (ImGui.MenuItem("Settings", string.Empty, open))
            _showSettings = !open;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Everything the wardrobe can be told to do differently.\n\n" +
                             "Opens in its own window, so it can sit beside the grid while\n" +
                             "you change something and watch what it does.");
    }

    private void DrawSelectModeItem()
    {
        var was = _selectMode;

        if (ImGui.MenuItem("Select Mode", string.Empty, _selectMode))
        {
            _selectMode = !was;
            if (_selectMode) EnterSelectMode();
            else             ExitSelectMode();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick several cards and act on them together.\n" +
                             "Cards show a tick box instead of their buttons while this is on.\n\n" +
                             "Works on whichever grid you are looking at — items, or outfits\n" +
                             "and design cards.");
    }

    /// <summary>
    /// The crop guide's three modes, chosen directly rather than cycled through.
    /// </summary>
    /// <remarks>
    /// The toolbar button could only turn it off and put back whichever mode you last had, so
    /// Always was reachable from Settings alone. A submenu costs the same click and shows all
    /// three, which is what the setting was for. The compact session view keeps its toggle — a
    /// menu is not reachable from a window that has no menu bar.
    /// </remarks>
    private void DrawCropGuideMenu()
    {
        if (!ImGui.BeginMenu("Crop Guide")) return;

        DrawCropGuideChoice(Configuration.CropGuideMode.Off, "Off",
            "Frame by eye. Nothing is drawn over the game.");
        DrawCropGuideChoice(Configuration.CropGuideMode.Sessions, "During Sessions",
            "Shows what a square screenshot will keep, and dims the rest,\n" +
            "while a screenshot session is running.");
        DrawCropGuideChoice(Configuration.CropGuideMode.Always, "Always",
            "Shows what a square screenshot will keep, and dims the rest,\n" +
            "whenever the game is showing.");

        ImGui.Separator();
        ImGui.TextDisabled("Never in the picture itself.");

        ImGui.EndMenu();
    }

    private void DrawCropGuideChoice(Configuration.CropGuideMode mode, string label, string tooltip)
    {
        var clicked = ImGui.MenuItem(label, string.Empty, _config.CropGuide == mode);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        if (!clicked) return;

        // Remembered so the compact session view's on/off toggle still puts back the mode that was
        // chosen here, rather than collapsing everyone onto Sessions
        if (mode != Configuration.CropGuideMode.Off)
            _cropGuideResume = mode;
        else if (_config.CropGuide != Configuration.CropGuideMode.Off)
            _cropGuideResume = _config.CropGuide;

        _config.CropGuide = mode;
        _config.Save();
    }

    /// <summary>
    /// The two card-size sliders, kept a click from the grid they resize.
    /// </summary>
    /// <remarks>
    /// Card size is judged by looking at the grid and dragging until it looks right, so it cannot
    /// live only in Settings four panels away — you would set it, close Settings, look, and go
    /// back. A submenu stays open while you drag, and the grid behind it redraws as you do.
    /// </remarks>
    private void DrawCardSizeMenu()
    {
        if (!ImGui.BeginMenu("Card Size")) return;

        DrawCardScaleSlider("Items", UiScale.S(220));
        ImGui.Spacing();
        DrawCardScaleSlider("Outfits", UiScale.S(220));

        ImGui.EndMenu();
    }

    // ── Session entries ───────────────────────────────────────────────────────

    private void DrawScreenshotSessionItem()
    {
        var can     = _session.CanStart;
        var clicked = ImGui.MenuItem("Screenshot Session", string.Empty, false, can);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(can
                ? "Automatically wear each unimaged item and watch for a\n" +
                  "new screenshot, then crop it to 1:1 and assign it.\n\n" +
                  "You take the screenshots." +
                  (_session.AutoEnabled
                      ? "\n\nSuper Screenshot Session below takes them for you."
                      : "\n\nSettings → Screenshot Sessions can have the wardrobe\n" +
                        "take them for you instead.")
                : "Nothing to photograph right now — every item already has a\n" +
                  "picture, or a session is already running.");

        if (!clicked) return;

        _showImageBrowser = false;
        _showSettings     = false;

        // Explicitly not the automatic kind. The two entries are what say which sort of run this
        // is, so pressing this one has to mean it even when the last was the other.
        _session.Auto = false;
        _session.Start();
    }

    /// <summary>
    /// Starts a session that photographs the whole wardrobe by itself.
    /// </summary>
    /// <remarks>
    /// Beside the ordinary one rather than folded into it, because the difference between them is
    /// the whole feature: one waits on you for every picture and the other waits on nobody, and
    /// which of those is about to happen should be the entry you pressed rather than the state of
    /// a tick box in a panel behind the character.
    /// <para>
    /// Hidden entirely until the feature is turned on, so the menu of anyone who has not asked for
    /// it does not carry an entry that only ever refuses.
    /// </para>
    /// </remarks>
    private void DrawSuperSessionItem()
    {
        if (!_session.AutoEnabled) return;

        var can = _session.CanStart;

        ImGui.PushStyleColor(ImGuiCol.Text, MenuRevert);
        var clicked = ImGui.MenuItem("Super Screenshot Session", string.Empty, false, can);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            // The two things that make a run come out wrong are both knowable before it starts, so
            // they are said here rather than found out three hundred pictures later
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

        if (!clicked) return;

        _showImageBrowser = false;
        _showSettings     = false;
        _session.StartSuper();
    }

    // ── The right-hand end ────────────────────────────────────────────────────

    /// <summary>
    /// What the last action did, and how much of the wardrobe is on screen.
    /// </summary>
    /// <remarks>
    /// Both used to sit in the run of buttons, where the status pushed everything after it along
    /// as it came and went. Here the count is pinned to the right edge and the status fills the
    /// gap before it, so neither can move anything else.
    /// </remarks>
    private void DrawMenuBarStatus()
    {
        var total = _config.WardrobeItems.Count;
        var count = total == 0
            ? string.Empty
            : _visibleCount == total
                ? $"{total} item{(total == 1 ? "" : "s")}"
                : $"{_visibleCount} of {total} items";

        // Placed to sit exactly against the right edge, so wrapping there would break it onto a
        // second line for a rounding error's worth of width
        UiLayout.PushNoWrap();

        if (!string.IsNullOrEmpty(_scanStatus))
        {
            ImGui.TextDisabled(_scanStatus);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("What the last Scan, Strip or Unequip did. Click to clear.");
            if (ImGui.IsItemClicked()) _scanStatus = string.Empty;
        }

        if (!string.IsNullOrEmpty(count))
        {
            var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X
                       - ImGui.CalcTextSize(count).X;
            if (rightX > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rightX);

            ImGui.TextDisabled(count);

            if (_visibleCount != total && ImGui.IsItemHovered())
                ImGui.SetTooltip("Filtered by the current search, slot, style, tag, worn or " +
                                 "favourites selection.");
        }

        UiLayout.PopWrap();
    }

    // ── Shared shapes ─────────────────────────────────────────────────────────

    /// <summary>
    /// The shortcut-column hint for the entries Ctrl changes, or nothing when it changes nothing.
    /// </summary>
    /// <remarks>
    /// One wording for both, because both mean the same thing: hold Ctrl and the base character
    /// comes off as well. They used to read "Ctrl: base too" and "Ctrl: base off", which are
    /// different enough to look like different behaviours while describing one.
    /// <para>
    /// Blank unless there is a base character for Ctrl to act on. A shortcut hint beside an entry
    /// where the key does nothing is worse than no hint: it is a control you go looking for.
    /// </para>
    /// </remarks>
    private string StripHint(bool baseRelevant)
    {
        var parts = new List<string>(2);

        if (baseRelevant)      parts.Add("Ctrl: base off");
        if (AnyAnimationWorn)  parts.Add("Shift: animations");

        return string.Join(" · ", parts);
    }

    /// <summary>Whether anything is running that only Shift would take off.</summary>
    /// <remarks>
    /// Same rule as the Ctrl hint: a modifier is only named where it has something to act on. With
    /// no animation, VFX or mount running, Shift changes nothing and saying otherwise sends people
    /// looking for a difference that is not there.
    /// </remarks>
    private bool AnyAnimationWorn =>
        _config.WornItems.Values.Any(id =>
            _config.WardrobeItems.Find(x => x.Id == id) is { Slot: var slot } && slot.IsModCategory());

    /// <summary>A menu entry that does something, with its explanation on hover.</summary>
    private static bool MenuAction(string label, string tooltip)
    {
        var clicked = ImGui.MenuItem(label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>
    /// A menu entry that opens or closes a side panel, ticked while its panel is showing.
    /// </summary>
    /// <remarks>
    /// The tick is what a menu would otherwise lose against the highlighted buttons these replaced:
    /// with three panels sharing one slot on the right, which of them is open has to be visible
    /// without opening the menu to find out.
    /// </remarks>
    private static void MenuPanelToggle(string label, ref bool state, string tooltip,
        Action? onActivate = null)
    {
        var was = state;

        if (ImGui.MenuItem(label, string.Empty, state))
        {
            state = !was;
            if (state) onActivate?.Invoke();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }
}
