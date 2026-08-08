using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;
using WardrobePlugin.Ui;

namespace WardrobePlugin.Services;

/// <summary>
/// Draws equipment-slot icons at a fixed size regardless of which icon set is in use.
/// </summary>
/// <remarks>
/// Both styles render into the same square box: game icons are stretched to it, and FontAwesome
/// glyphs are centred within it. Without that the two sets would disagree on size — a texture is
/// whatever pixels it contains, while a glyph is sized by the font — and switching styles would
/// reflow every row that uses one.
/// </remarks>
public class SlotIconService
{
    /// <summary>Edge length of the square every icon is drawn into, in unscaled pixels.</summary>
    public const float IconSize = 20f;

    private readonly Configuration     _config;
    private readonly ITextureProvider  _textures;
    private readonly ItemLookupService _itemLookup;
    private readonly IPluginLog        _log;

    public SlotIconService(Configuration config, ITextureProvider textures,
        ItemLookupService itemLookup, IPluginLog log)
    {
        _config     = config;
        _textures   = textures;
        _itemLookup = itemLookup;
        _log        = log;
    }

    public bool Enabled => _config.SlotIconsEnabled;

    private static readonly Dictionary<EquipSlot, FontAwesomeIcon> FontIcons = new()
    {
        { EquipSlot.Head,      FontAwesomeIcon.HatCowboy },
        { EquipSlot.Body,      FontAwesomeIcon.Tshirt },
        { EquipSlot.Hands,     FontAwesomeIcon.Mitten },
        { EquipSlot.Legs,      FontAwesomeIcon.Socks },
        { EquipSlot.Feet,      FontAwesomeIcon.ShoePrints },
        { EquipSlot.Ears,      FontAwesomeIcon.Gem },
        { EquipSlot.Neck,      FontAwesomeIcon.Link },
        { EquipSlot.Wrists,    FontAwesomeIcon.Circle },
        { EquipSlot.RingRight, FontAwesomeIcon.Ring },
        { EquipSlot.RingLeft,  FontAwesomeIcon.Ring },
        { EquipSlot.MainHand,  FontAwesomeIcon.Khanda },
        { EquipSlot.OffHand,   FontAwesomeIcon.Shield },
        { EquipSlot.Hair,      FontAwesomeIcon.Spa },
        { EquipSlot.Face,      FontAwesomeIcon.UserNinja },
        { EquipSlot.Tail,      FontAwesomeIcon.Paw },
        { EquipSlot.VieraEars, FontAwesomeIcon.Cat },
        { EquipSlot.Skin,      FontAwesomeIcon.Fingerprint },

        // Mod categories. No game icon exists for any of these — GetSlotIcon reads an item's UI
        // category and none of them is an item — so the glyph is all there is.
        { EquipSlot.Animation, FontAwesomeIcon.Running },
        { EquipSlot.Vfx,       FontAwesomeIcon.Magic },
        { EquipSlot.Mount,     FontAwesomeIcon.Horse },
    };

    /// <summary>Bounds on the size multipliers, so a stored value cannot make icons invisible or
    /// large enough to swallow the window.</summary>
    public const float MinScale = 0.5f;
    public const float MaxScale = 3f;

    /// <summary>The size icons are drawn at with no user scaling — the baseline the rest measure from.</summary>
    /// <remarks>
    /// Scaled with the rest of the layout, so an icon keeps its proportion to the text beside it at
    /// any Global Font Scale. See <see cref="Ui.UiScale"/> for what the factor is tied to.
    /// </remarks>
    public static float BaseScaledSize => UiScale.S(IconSize);

    /// <summary>Edge length for icons drawn on item cards and inline.</summary>
    public float ScaledSize => BaseScaledSize * Math.Clamp(_config.SlotIconScale, MinScale, MaxScale);

    /// <summary>
    /// Edge length for the slot filter row, which is sized separately.
    /// </summary>
    /// <remarks>
    /// The row and the cards want opposite things: the row is a fixed width with as many slots on it
    /// as will fit, so smaller icons mean fewer spill into the More dropdown, while a card has one
    /// icon and only legibility to care about. One slider for both would trade them off against each
    /// other for no reason.
    /// </remarks>
    public float ScaledRowSize => BaseScaledSize * Math.Clamp(_config.SlotIconRowScale, MinScale, MaxScale);

    // ── Custom icons ──────────────────────────────────────────────────────────

    /// <summary>Image types accepted for a custom icon, in the order a name collision prefers them.</summary>
    private static readonly string[] IconExtensions = { ".png", ".webp", ".jpg", ".jpeg", ".bmp", ".tga" };

    /// <summary>Folder the current scan was built from, so a change to the setting is noticed.</summary>
    private string _scannedFolder = string.Empty;

    /// <summary>Slot → file found for it. Rebuilt by <see cref="Rescan"/>, never per frame.</summary>
    private readonly Dictionary<EquipSlot, string> _customPaths = new();

    /// <summary>Slot → loaded texture, populated lazily the first time a slot is drawn.</summary>
    private readonly Dictionary<EquipSlot, ISharedImmediateTexture?> _customTextures = new();

    /// <summary>Every slot a custom icon can be supplied for.</summary>
    public static IEnumerable<EquipSlot> IconSlots =>
        EquipSlotEx.All.Concat(EquipSlotEx.ModCategories);

    /// <summary>The file name to give a slot's icon — what the settings panel tells the user to use.</summary>
    public static string ExpectedFileName(EquipSlot slot) => $"{slot}.png";

    /// <summary>True when a custom icon file was found for this slot.</summary>
    public bool HasCustomIcon(EquipSlot slot)
    {
        EnsureScanned();
        return _customPaths.ContainsKey(slot);
    }

    /// <summary>How many slots the folder currently supplies.</summary>
    public int CustomIconCount
    {
        get
        {
            EnsureScanned();
            return _customPaths.Count;
        }
    }

    /// <summary>
    /// Rescans the folder if the setting has changed since the last scan.
    /// </summary>
    /// <remarks>
    /// A string comparison, deliberately — this runs from the draw path, and a slot icon is drawn on
    /// every card of every frame. Touching the filesystem here is what
    /// <see cref="Ui.PluginUi"/>'s image cache exists to avoid; see the note on <c>DrawItemImage</c>.
    /// </remarks>
    private void EnsureScanned()
    {
        if (_scannedFolder == _config.CustomIconFolder) return;
        Rescan();
    }

    /// <summary>
    /// Reads the folder and works out which file belongs to which slot.
    /// </summary>
    /// <remarks>
    /// One directory listing, then names are matched against slots — rather than probing for each
    /// slot in each extension, which would be a hundred-odd stat calls. Called on demand and by the
    /// Rescan button, since files are added to the folder outside the game.
    /// </remarks>
    public void Rescan()
    {
        _scannedFolder = _config.CustomIconFolder;
        _customPaths.Clear();
        _customTextures.Clear();

        if (string.IsNullOrWhiteSpace(_scannedFolder) || !Directory.Exists(_scannedFolder)) return;

        string[] files;
        try
        {
            files = Directory.GetFiles(_scannedFolder);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, $"[Wardrobe] Could not read custom icon folder '{_scannedFolder}'");
            return;
        }

        // Bare file name → best path for it. "Head.png" beats "Head.jpg" so a folder holding both
        // behaves predictably rather than depending on directory order.
        var byName = new Dictionary<string, (string Path, int Rank)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var rank = Array.FindIndex(IconExtensions,
                e => e.Equals(Path.GetExtension(file), StringComparison.OrdinalIgnoreCase));
            if (rank < 0) continue;

            var stem = Path.GetFileNameWithoutExtension(file);
            if (!byName.TryGetValue(stem, out var existing) || rank < existing.Rank)
                byName[stem] = (file, rank);
        }

        foreach (var slot in IconSlots)
        {
            // Either the enum name or the display name without its spaces, so both "RingRight" and
            // "RightRing" find the same slot — the two are not obviously interchangeable to someone
            // naming files, and guessing wrong is invisible until the icon fails to appear.
            if (byName.TryGetValue(slot.ToString(), out var hit) ||
                byName.TryGetValue(slot.DisplayName().Replace(" ", string.Empty), out hit))
                _customPaths[slot] = hit.Path;
        }

        _log.Debug($"[Wardrobe] Custom icons: {_customPaths.Count} slot(s) matched in '{_scannedFolder}'");
    }

    /// <summary>
    /// The user's own icon for this slot, if the folder supplies one and it loads.
    /// </summary>
    /// <remarks>
    /// Returns the wrap rather than a handle so callers can draw it through
    /// <see cref="Ui.ImageDraw"/> — a supplied image will not always be square, and ImGui stretches
    /// whatever it is handed into the rectangle it is given.
    /// </remarks>
    public bool TryGetCustomIcon(EquipSlot slot, out IDalamudTextureWrap? wrap)
    {
        wrap = null;
        EnsureScanned();

        if (!_customPaths.TryGetValue(slot, out var path)) return false;

        if (!_customTextures.TryGetValue(slot, out var texture))
        {
            try
            {
                texture = _textures.GetFromFile(path);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"[Wardrobe] Could not load custom icon '{path}'");
                texture = null;
            }

            // Cached either way, so a file that cannot be read is not retried every frame
            _customTextures[slot] = texture;
        }

        wrap = texture?.GetWrapOrDefault();
        return wrap != null;
    }

    /// <summary>
    /// Texture handle for this slot's game icon, if game icons are selected and one is loaded.
    /// </summary>
    public bool TryGetGameIcon(EquipSlot slot, out ImTextureID handle)
    {
        handle = default;
        if (_config.SlotIconStyle != SlotIconStyle.GameIcons) return false;

        // Gear slots come from item UI categories; customisation slots from character-creation
        // data. Anything neither covers (tail, ears, skin) returns false and falls back to a glyph.
        var iconId = _itemLookup.GetSlotIcon(slot) ?? _itemLookup.GetCustomizationIcon(slot);
        if (iconId is not { } id) return false;

        var wrap = _textures.GetFromGameIcon(new GameIconLookup(id)).GetWrapOrDefault();
        if (wrap == null) return false; // missing or still loading

        handle = wrap.Handle;
        return true;
    }

    /// <summary>FontAwesome glyph for this slot, used directly or as a fallback for game icons.</summary>
    public bool TryGetFontIcon(EquipSlot slot, out string glyph)
    {
        if (FontIcons.TryGetValue(slot, out var icon))
        {
            glyph = icon.ToIconString();
            return true;
        }
        glyph = string.Empty;
        return false;
    }

    /// <summary>
    /// Draws the icon for a slot inline, into a fixed square. Returns false if nothing could be
    /// drawn, so callers can fall back to the slot name.
    /// </summary>
    public bool Draw(EquipSlot slot) => Draw(slot, ScaledSize);

    /// <param name="size">Square edge length to draw into, for callers with their own sizing.</param>
    public bool Draw(EquipSlot slot, float size)
    {
        if (!Enabled) return false;

        // The user's own icon wins over both built-in sets, so supplying one file replaces one icon
        // and leaves every other slot on whichever set is selected
        if (TryGetCustomIcon(slot, out var custom) && custom != null)
        {
            ImageDraw.Square(custom, size);
            return true;
        }

        if (TryGetGameIcon(slot, out var handle))
        {
            ImGui.Image(handle, new Vector2(size, size));
            return true;
        }

        return DrawFontIcon(slot, size);
    }

    /// <summary>Draws a FontAwesome glyph centred inside the same square a game icon would occupy.</summary>
    private static bool DrawFontIcon(EquipSlot slot, float size)
    {
        if (!FontIcons.TryGetValue(slot, out var icon)) return false;

        var text  = icon.ToIconString();
        var start = ImGui.GetCursorPos();

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
        {
            var glyph = ImGui.CalcTextSize(text);
            ImGui.SetCursorPos(new Vector2(
                start.X + (size - glyph.X) * 0.5f,
                start.Y + (size - glyph.Y) * 0.5f));
            ImGui.TextUnformatted(text);
        }

        // Claim exactly the square, so layout matches the game-icon path regardless of glyph size
        ImGui.SetCursorPos(start);
        ImGui.Dummy(new Vector2(size, size));
        return true;
    }
}
