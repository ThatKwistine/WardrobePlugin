using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using WardrobePlugin.Models;

namespace WardrobePlugin.Ipc;

public class GlamourerIpc : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IObjectTable _objects;

    // GetState(objectIndex, key) → (GlamourerApiEc, JObject?)
    // Used to read current equipment state per slot.
    private readonly ICallGateSubscriber<int, uint, (int, JObject?)> _getState;

    // GetStateBase64(objectIndex, key) → (GlamourerApiEc, string? base64)
    private readonly ICallGateSubscriber<int, uint, (int, string?)> _getStateBase64;

    // ApplyState(state, objectIndex, key, applyFlags) → GlamourerApiEc
    // applyFlags 14uL = Equipment(2) | Customization(4) | Lock(8)
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _applyState;

    // RevertState(objectIndex, key, applyFlags) → GlamourerApiEc
    // applyFlags 6uL = Equipment(2) | Customization(4)
    private readonly ICallGateSubscriber<int, uint, ulong, int> _revertState;

    // SetItem(objectIndex, slot, itemId, stains, key, applyFlags) → GlamourerApiEc
    // stains must be List<byte> (not byte[]) — byte[] serializes as base64 which Glamourer can't deserialize
    private readonly ICallGateSubscriber<int, byte, ulong, List<byte>, uint, ulong, int> _setItem;

    // SetMetaState(objectIndex, types, newValue, key, flags) → GlamourerApiEc
    // types is a MetaFlag bitfield: Wetness=0x01, HatState=0x02, VisorState=0x04, WeaponState=0x08
    private readonly ICallGateSubscriber<int, ulong, bool, uint, ulong, int> _setMetaState;

    // GetDesignList.V2() → Dictionary<Guid, string> of design ID to display name
    private readonly ICallGateSubscriber<Dictionary<Guid, string>> _getDesignList;

    // ApplyDesign(designId, objectIndex, key, applyFlags) → GlamourerApiEc
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> _applyDesign;

    // ApiVersion.V2() → (major, minor). Throws if Glamourer is not loaded.
    private readonly ICallGateSubscriber<(int Major, int Minor)> _apiVersion;

    // OpenActorIndex(objectIndex) → void — opens Glamourer's main window on the Actors tab with that
    // actor selected. Typed with a trailing object? like the other actions, since a subscriber's
    // last type argument is the return type and InvokeAction ignores it.
    private readonly ICallGateSubscriber<int, object?> _openActorIndex;

    private const ulong WeaponStateFlag   = 0x08uL;
    private const ulong CustomizationFlag = 0x04uL;
    private const ulong EquipmentFlag     = 0x02uL;

    // Property names Glamourer might use for the hairstyle inside its Customize block. The exact
    // shape is not documented, so several spellings are tried and the real keys are logged on a miss.
    private static readonly string[] HairKeys = { "Hairstyle", "HairStyle", "Hair" };

    private bool _loggedCustomizeShape;

    private static readonly List<byte> NoStains = new() { 0, 0 };
    private const int PlayerIndex = 0;

    public GlamourerIpc(IDalamudPluginInterface pi, IPluginLog log, IObjectTable objects)
    {
        _log     = log;
        _objects = objects;

        _getState       = pi.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
        _getStateBase64 = pi.GetIpcSubscriber<int, uint, (int, string?)>("Glamourer.GetStateBase64");
        _applyState     = pi.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");
        _revertState    = pi.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
        _setItem        = pi.GetIpcSubscriber<int, byte, ulong, List<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
        _setMetaState   = pi.GetIpcSubscriber<int, ulong, bool, uint, ulong, int>("Glamourer.SetMetaState");
        _getDesignList  = pi.GetIpcSubscriber<Dictionary<Guid, string>>("Glamourer.GetDesignList.V2");
        _applyDesign    = pi.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        _apiVersion     = pi.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        _openActorIndex = pi.GetIpcSubscriber<int, object?>("Glamourer.OpenActorIndex");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Whether Glamourer is loaded and responding, with its version for display.</summary>
    public (bool Available, string Version) CheckAvailable()
    {
        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();
            return (true, $"{major}.{minor}");
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    /// <summary>
    /// Returns all equipment slot item IDs currently applied by Glamourer on the local player.
    /// Keys are Glamourer slot names (Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RFinger, LFinger, MainHand, OffHand).
    /// Returns null if Glamourer is unavailable or the player is not loaded.
    /// </summary>
    public Dictionary<string, ulong>? GetAllEquipment()
    {
        if (_objects.LocalPlayer == null) return null;
        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null) return null;

            var equipment = state["Equipment"] as JObject;
            if (equipment == null) return null;

            var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in equipment.Properties())
            {
                var itemIdToken = prop.Value["ItemId"];
                if (itemIdToken != null)
                    result[prop.Name] = itemIdToken.Value<ulong>();
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetState failed");
            return null;
        }
    }

    /// <summary>
    /// Everything Glamourer currently has equipped, with both dye channels, by slot.
    /// </summary>
    /// <remarks>
    /// The same Equipment block <see cref="GetAllEquipment"/> reads, keyed by our own slots and
    /// carrying the stains as well — enough to put a plain game item back on later without a mod
    /// behind it. Slots holding nothing are left out: Glamourer marks an empty slot with an item ID
    /// near the top of the range rather than zero, which would otherwise be stored as a real item
    /// and fail to equip.
    /// </remarks>
    public Dictionary<EquipSlot, (ulong ItemId, byte Stain1, byte Stain2)> GetEquippedPieces()
    {
        var result = new Dictionary<EquipSlot, (ulong, byte, byte)>();
        if (_objects.LocalPlayer == null) return result;

        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state?["Equipment"] is not JObject equipment) return result;

            foreach (var slot in EquipSlotEx.All)
            {
                var name = ToSlotName(slot);
                if (name.Length == 0) continue;
                if (equipment[name] is not JObject entry) continue;

                var itemId = entry["ItemId"]?.Value<ulong>() ?? 0;
                if (itemId == 0 || itemId >= NothingItemIdFloor) continue;

                result[slot] = (itemId,
                    entry["Stain"]?.Value<byte>()  ?? 0,
                    entry["Stain2"]?.Value<byte>() ?? 0);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetEquippedPieces failed");
        }

        return result;
    }

    /// <summary>
    /// Item IDs at or above this are Glamourer's markers for an empty slot rather than real items.
    /// </summary>
    /// <remarks>
    /// They sit just under <see cref="uint.MaxValue"/> — 4294967164 and its neighbours — while real
    /// game items are five-figure row IDs, so the gap between the two is enormous and a floor is
    /// safe. Checked rather than assumed because storing one as an item would produce an outfit that
    /// silently fails to equip a piece.
    /// </remarks>
    private const ulong NothingItemIdFloor = 4_000_000_000;

    /// <summary>
    /// Locates the hairstyle value inside a Glamourer state's Customize block.
    /// </summary>
    /// <remarks>
    /// Handles both a bare number and a wrapped <c>{ "Value": n }</c>, since the shape is not
    /// documented. On a miss the actual keys are logged once, so a single run reveals the real
    /// layout rather than leaving this failing silently.
    /// </remarks>
    private JToken? FindHairToken(JObject state)
    {
        if (state["Customize"] is not JObject customize)
            return null;

        foreach (var key in HairKeys)
        {
            if (customize[key] is not { } token) continue;
            return token is JObject wrapped ? wrapped["Value"] : token;
        }

        if (!_loggedCustomizeShape)
        {
            _loggedCustomizeShape = true;
            _log.Warning($"[Wardrobe] No hairstyle key in Glamourer Customize. Keys present: " +
                         $"{string.Join(", ", customize.Properties().Select(p => p.Name))}");
        }
        return null;
    }

    /// <summary>All Glamourer designs, as ID and display name.</summary>
    public IList<(Guid Id, string Name)> GetDesigns()
    {
        try
        {
            return _getDesignList.InvokeFunc()
                .Select(kvp => (kvp.Key, kvp.Value))
                .OrderBy(d => d.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetDesignList failed");
            return Array.Empty<(Guid, string)>();
        }
    }

    /// <summary>
    /// Applies only the customisation half of a design, leaving equipment untouched.
    /// </summary>
    /// <remarks>
    /// Used as the baseline to return to when a customisation mod is reverted. Passing only the
    /// Customization flag is what makes the design's equipment irrelevant, so a design saved for
    /// this purpose does not disturb whatever the wardrobe currently has equipped.
    /// </remarks>
    public bool ApplyDesignCustomization(Guid designId) =>
        ApplyDesign(designId, CustomizationFlag, "customisation only");

    /// <summary>
    /// Applies a design whole — its equipment as well as its customisations.
    /// </summary>
    /// <remarks>
    /// Only ever from a button the user pressed. Nothing applies a design's equipment on its own:
    /// the paths that reach for a design are putting a character's face back, and dressing them
    /// from it at the same time would undo whatever the wardrobe had just equipped.
    /// </remarks>
    public bool ApplyDesignFull(Guid designId) =>
        ApplyDesign(designId, EquipmentFlag | CustomizationFlag, "equipment and customisation");

    private bool ApplyDesign(Guid designId, ulong flags, string what)
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            var ec = _applyDesign.InvokeFunc(designId, PlayerIndex, 0u, flags);
            _log.Debug($"[Wardrobe] ApplyDesign ({what}) {designId} → ec={ec}");
            return ec == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer ApplyDesign failed");
            return false;
        }
    }

    /// <summary>
    /// Model race code for the local player — the number in <c>chara/human/c{code}/</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the customise data: race, gender, and for Hyur the tribe, since Midlander and
    /// Highlander are separate models. Needed because hairstyle numbering is per-race, so the same
    /// hair mod is a different number on each one.
    /// </remarks>
    public int? GetPlayerRaceCode()
    {
        var player = _objects.LocalPlayer;
        if (player == null) return null;

        try
        {
            var customize = player.Customize;
            if (customize.Length < 5) return null;

            var race   = customize[0];
            var gender = customize[1]; // 0 = male, 1 = female
            var tribe  = customize[4];
            var female = gender == 1;

            return race switch
            {
                1 => tribe == 2 ? (female ? 401 : 301)   // Hyur: Highlander
                                : (female ? 201 : 101),  //       Midlander
                2 => female ? 601  : 501,   // Elezen
                3 => female ? 1201 : 1101,  // Lalafell
                4 => female ? 801  : 701,   // Miqo'te
                5 => female ? 1001 : 901,   // Roegadyn
                6 => female ? 1401 : 1301,  // Au Ra
                7 => female ? 1601 : 1501,  // Hrothgar
                8 => female ? 1801 : 1701,  // Viera
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Could not read player customise data");
            return null;
        }
    }

    /// <summary>Current hairstyle number of the local player, or null if it cannot be read.</summary>
    public int? GetHairstyle()
    {
        if (_objects.LocalPlayer == null) return null;
        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null) return null;

            var token = FindHairToken(state);
            return token is { Type: JTokenType.Integer } ? token.Value<int>() : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetHairstyle failed");
            return null;
        }
    }

    /// <summary>
    /// Switches the local player to a hairstyle number, so a hair mod targeting it becomes visible.
    /// </summary>
    public bool SetHairstyle(int hairstyle)
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null)
            {
                _log.Debug($"[Wardrobe] SetHairstyle: GetState returned ec={ec}");
                return false;
            }

            var token = FindHairToken(state);
            if (token == null) return false;

            if (token.Type == JTokenType.Integer && token.Value<int>() == hairstyle)
            {
                _log.Debug($"[Wardrobe] SetHairstyle: already {hairstyle}");
                return true;
            }

            token.Replace(new JValue(hairstyle));

            // The whole state is sent back, but only customisations are applied — equipment is
            // left to the normal SetItem path.
            var rc = _applyState.InvokeFunc(state, PlayerIndex, 0u, CustomizationFlag);
            _log.Debug($"[Wardrobe] SetHairstyle → {hairstyle}, ApplyState ec={rc}");
            return rc == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer SetHairstyle failed");
            return false;
        }
    }

    // ── Advanced dyes ─────────────────────────────────────────────────────────

    /// <summary>
    /// The advanced dye rows Glamourer currently has on the local player.
    /// </summary>
    /// <remarks>
    /// Glamourer has no IPC for advanced dyes — they travel inside the state as a <c>Materials</c>
    /// object, which <c>GetState</c> includes because it converts with all application rules. Each
    /// property is a packed key (draw object type, slot index, material index, row index) against a
    /// colour row, so a row belongs to a <em>slot</em> rather than to the item in it: change the
    /// piece and the row describes a different model's material.
    /// <para>
    /// Returned as raw JSON for the caller to store and hand back, deliberately. The row's contents
    /// are Glamourer's business — it owns the editor, and nothing here needs to understand what
    /// specular strength means to store one.
    /// </para>
    /// </remarks>
    public JObject? GetAdvancedDyes()
    {
        if (_objects.LocalPlayer == null) return null;
        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null) return null;

            return state["Materials"] as JObject;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetAdvancedDyes failed");
            return null;
        }
    }

    /// <summary>
    /// Opens Glamourer's window on the local player, ready for its advanced dye editor.
    /// </summary>
    /// <remarks>
    /// The nearest the API gets to the advanced dye popup. The popup itself cannot be opened from
    /// outside — it is drawn per slot from inside Glamourer's own panel and needs an actor and state
    /// object — so this lands the user on the Actors tab with their character selected, one palette
    /// icon away. That is the whole point of the button: Glamourer owns the editor, and the wardrobe
    /// should hand over rather than imitate it.
    /// </remarks>
    public bool OpenOnPlayer()
    {
        try
        {
            _openActorIndex.InvokeAction(PlayerIndex);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer OpenActorIndex failed");
            return false;
        }
    }

    /// <summary>
    /// Advanced dye rows belonging to one slot, ready to store against an item.
    /// </summary>
    /// <remarks>
    /// Filtered by slot rather than kept whole, because the wardrobe stores these per item: a row
    /// captured while a hat was on has nothing to do with the boots, and keeping the whole block
    /// against every item would have them overwrite each other on the way back.
    /// </remarks>
    public Dictionary<string, string> CaptureAdvancedDyes(EquipSlot slot)
    {
        var captured = new Dictionary<string, string>();

        var materials = GetAdvancedDyes();
        if (materials == null) return captured;

        foreach (var prop in materials.Properties())
        {
            if (!KeyBelongsToSlot(prop.Name, slot)) continue;
            captured[prop.Name] = prop.Value.ToString(Newtonsoft.Json.Formatting.None);
        }

        return captured;
    }

    /// <summary>
    /// Puts stored advanced dye rows back onto the character.
    /// </summary>
    /// <remarks>
    /// The rows are merged into the state as it is now and the whole thing is handed back, the same
    /// approach <see cref="SetHairstyle"/> takes. Sending only the rows is not an option: there is
    /// no material-shaped call to send them to, and a state stripped to one key would be read as
    /// everything else having been cleared.
    /// </remarks>
    public bool ApplyAdvancedDyes(IReadOnlyDictionary<string, string> rows)
    {
        if (rows.Count == 0) return true;
        if (_objects.LocalPlayer == null) return false;

        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null) return false;

            if (state["Materials"] is not JObject materials)
            {
                materials = new JObject();
                state["Materials"] = materials;
            }

            foreach (var (key, json) in rows)
            {
                try
                {
                    materials[key] = JToken.Parse(json);
                }
                catch (Exception ex)
                {
                    // One unreadable row must not cost the rest of them
                    _log.Warning(ex, $"[Wardrobe] Skipping unreadable advanced dye row '{key}'");
                }
            }

            var rc = _applyState.InvokeFunc(state, PlayerIndex, 0u, EquipmentFlag);
            _log.Debug($"[Wardrobe] ApplyAdvancedDyes: {rows.Count} row(s) → ec={rc}");
            return rc == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer ApplyAdvancedDyes failed");
            return false;
        }
    }

    /// <summary>
    /// Puts the given rows back to their game values.
    /// </summary>
    /// <remarks>
    /// Needed because a row belongs to a slot rather than to an item: left alone, dyes captured for
    /// one piece would go on describing the material of whatever is worn there next. Glamourer's own
    /// <c>Revert</c> flag on a row is the documented way to say so, which is why the stored row is
    /// sent back with that set rather than simply being dropped.
    /// </remarks>
    public bool RevertAdvancedDyes(IReadOnlyDictionary<string, string> rows)
    {
        if (rows.Count == 0) return true;

        var reverted = new Dictionary<string, string>(rows.Count);

        foreach (var (key, json) in rows)
        {
            try
            {
                var row = JObject.Parse(json);
                row["Revert"]  = true;
                row["Enabled"] = true;
                reverted[key]  = row.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"[Wardrobe] Could not build a revert for advanced dye row '{key}'");
            }
        }

        return ApplyAdvancedDyes(reverted);
    }

    /// <summary>
    /// The diffuse colour of a stored row, packed for ImGui, or null if it has none.
    /// </summary>
    /// <remarks>
    /// Only so a captured row can be shown as a swatch. Diffuse is the one field of a colour row
    /// that reads as "the colour" to a person — specular and emissive describe how it catches light
    /// rather than what shade it is — so it is the honest single square to stand for the row.
    /// A row's values are floats and are not clamped in Glamourer, so anything over 1 is pinned here
    /// rather than wrapping round to a wrong colour.
    /// </remarks>
    public static uint? RowDiffuseColour(string json)
    {
        try
        {
            var row = JObject.Parse(json);

            // Flat, one property per channel — confirmed against a real row rather than assumed:
            // {"Revert":false,"Mode":"Legacy","DiffuseR":0.58,"DiffuseG":0.0,"DiffuseB":0.0,…}
            var r = row["DiffuseR"];
            var g = row["DiffuseG"];
            var b = row["DiffuseB"];
            if (r == null || g == null || b == null) return null;

            static uint Channel(JToken? v) => (uint)(Math.Clamp(v?.Value<float>() ?? 0f, 0f, 1f) * 255f);

            // ImGui packs as 0xAABBGGRR
            return 0xFF000000u | (Channel(b) << 16) | (Channel(g) << 8) | Channel(r);
        }
        catch
        {
            return null; // a row that will not parse simply has no swatch
        }
    }

    /// <summary>
    /// Whether an advanced dye row's key describes a material of the given slot.
    /// </summary>
    /// <remarks>
    /// The key is a packed uint written as hex: draw object type in bits 24-31, slot index in 16-23,
    /// then material and row. Only the first two matter here. Weapons are their own draw object
    /// rather than a slot on the body, which is why they are not simply indices 10 and 11.
    /// </remarks>
    public static bool KeyBelongsToSlot(string key, EquipSlot slot)
    {
        if (SlotMaterialIndex(slot) is not { } wanted) return false;
        if (!uint.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;

        return (byte)(packed >> 24) == wanted.DrawObject && (byte)(packed >> 16) == wanted.SlotIndex;
    }

    /// <summary>Draw object and slot index a slot's material keys carry, or null if it has none.</summary>
    /// <remarks>
    /// Mirrors Glamourer's <c>MaterialValueIndex.FromSlot</c>, which indexes the human draw object by
    /// the game's model slot order. Note the rings: right is 8 and left is 9, so getting the pair
    /// the wrong way round would silently capture one ring's rows against the other.
    /// </remarks>
    private static (byte DrawObject, byte SlotIndex)? SlotMaterialIndex(EquipSlot slot) => slot switch
    {
        EquipSlot.Head      => (DrawObjectHuman, (byte)0),
        EquipSlot.Body      => (DrawObjectHuman, (byte)1),
        EquipSlot.Hands     => (DrawObjectHuman, (byte)2),
        EquipSlot.Legs      => (DrawObjectHuman, (byte)3),
        EquipSlot.Feet      => (DrawObjectHuman, (byte)4),
        EquipSlot.Ears      => (DrawObjectHuman, (byte)5),
        EquipSlot.Neck      => (DrawObjectHuman, (byte)6),
        EquipSlot.Wrists    => (DrawObjectHuman, (byte)7),
        EquipSlot.RingRight => (DrawObjectHuman, (byte)8),
        EquipSlot.RingLeft  => (DrawObjectHuman, (byte)9),
        EquipSlot.MainHand  => (DrawObjectMainhand, (byte)0),
        EquipSlot.OffHand   => (DrawObjectOffhand,  (byte)0),
        _                   => null,
    };

    /// <summary>Glamourer's DrawObjectType values, as they appear in a material key.</summary>
    private const byte DrawObjectHuman    = 1;
    private const byte DrawObjectMainhand = 2;
    private const byte DrawObjectOffhand  = 3;

    // The advanced dye probe that used to live here — a button that wrote Glamourer's Materials
    // block to the log — is gone with the feature's graduation out of Experimental. It existed to
    // settle an undocumented layout by reading a real one, and that question is answered. If a
    // Glamourer update ever unsettles it, it is a dozen lines over GetAdvancedDyes, and git has it.

    /// <summary>
    /// Snapshot the local player's current Glamourer state as a base64 blob.
    /// Returns null if the player is not loaded or Glamourer is unavailable.
    /// </summary>
    public string? CaptureCurrentState()
    {
        if (_objects.LocalPlayer == null) return null;
        try
        {
            var (ec, b64) = _getStateBase64.InvokeFunc(PlayerIndex, 0u);
            return ec == 0 ? b64 : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer GetStateBase64 failed");
            return null;
        }
    }

    /// <summary>
    /// Apply a previously captured state blob to the local player.
    /// Applies equipment, customization, and locks the state.
    /// </summary>
    public bool ApplyState(string base64)
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            // Equipment(2) | Customization(4) | Lock(8) = 14
            var ec = _applyState.InvokeFunc(base64, PlayerIndex, 0u, 14uL);
            return ec == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer ApplyState failed");
            return false;
        }
    }

    /// <summary>
    /// Revert the local player back to their unmodified game state.
    /// </summary>
    public bool RevertState()
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            // Equipment(2) | Customization(4) = 6
            var ec = _revertState.InvokeFunc(PlayerIndex, 0u, 6uL);
            return ec == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer RevertState failed");
            return false;
        }
    }

    /// <summary>
    /// Equip a specific FFXIV game item in the given slot on the local player via Glamourer.
    /// </summary>
    /// <param name="stain1">Primary dye channel, 0 for undyed.</param>
    /// <param name="stain2">Secondary dye channel, 0 for undyed.</param>
    public bool SetItem(EquipSlot slot, ulong itemId, byte stain1 = 0, byte stain2 = 0)
    {
        if (_objects.LocalPlayer == null)
        {
            _log.Warning("[Wardrobe] Glamourer SetItem: local player is null");
            return false;
        }
        var apiSlot = ToApiEquipSlot(slot);
        if (apiSlot == 0)
        {
            _log.Warning($"[Wardrobe] Glamourer SetItem: no slot mapping for {slot}");
            return false;
        }
        try
        {
            // Must be List<byte>: a byte[] serialises as base64, which Glamourer cannot read back
            var stains = stain1 == 0 && stain2 == 0 ? NoStains : new List<byte> { stain1, stain2 };

            var ec = _setItem.InvokeFunc(PlayerIndex, apiSlot, itemId, stains, 0u, 0uL);
            _log.Debug($"[Wardrobe] Glamourer SetItem slot={slot}(api={apiSlot}) itemId={itemId} " +
                       $"stains={stain1},{stain2} → ec={ec}");
            return ec == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer SetItem threw — IPC may not be registered");
            return false;
        }
    }

    /// <summary>
    /// Show or hide the weapon model on the local player via Glamourer's WeaponState meta flag.
    /// Pass false to hide, true to restore.
    /// </summary>
    private bool _loggedWeaponShape;

    /// <summary>
    /// Whether the weapon is currently shown, or null if it cannot be read.
    /// </summary>
    /// <remarks>
    /// Needed so the plugin can put weapon visibility back the way the user had it, rather than
    /// assuming it was visible — plenty of people keep weapons hidden permanently.
    /// The state layout is undocumented and turned out not to be top-level, so rather than guess a
    /// path this searches for a property named after the weapon anywhere in the object, accepting
    /// either a bare bool or Glamourer's nested { "Show": bool } shape.
    /// </remarks>
    public bool? GetWeaponVisible()
    {
        if (_objects.LocalPlayer == null) return null;
        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null) return null;

            var found = FindWeaponVisibility(state);
            if (found.HasValue) return found;

            if (!_loggedWeaponShape)
            {
                _loggedWeaponShape = true;
                _log.Debug($"[Wardrobe] Weapon visibility not found. State layout: {DescribeShape(state)}");
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetWeaponVisible failed");
            return null;
        }
    }

    private static bool? FindWeaponVisibility(JToken node)
    {
        if (node is not JObject obj) return null;

        foreach (var prop in obj.Properties())
        {
            if (prop.Name.Contains("weapon", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.Type == JTokenType.Boolean)
                    return prop.Value.Value<bool>();

                if (prop.Value is JObject inner)
                {
                    if (inner["Show"]  is { Type: JTokenType.Boolean } show)  return show.Value<bool>();
                    if (inner["Value"] is { Type: JTokenType.Boolean } value) return value.Value<bool>();
                }
            }

            var nested = FindWeaponVisibility(prop.Value);
            if (nested.HasValue) return nested;
        }

        return null;
    }

    /// <summary>Two levels of property names, enough to identify the real layout from one log line.</summary>
    private static string DescribeShape(JObject state) =>
        string.Join(" | ", state.Properties().Select(p =>
            p.Value is JObject child
                ? $"{p.Name}{{{string.Join(",", child.Properties().Select(c => c.Name))}}}"
                : p.Name));

    public bool SetWeaponVisible(bool visible)
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            var ec = _setMetaState.InvokeFunc(PlayerIndex, WeaponStateFlag, visible, 0u, 0uL);
            _log.Debug($"[Wardrobe] Glamourer SetWeaponVisible={visible} → ec={ec}");
            return ec == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer SetMetaState (weapon) failed");
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Maps our EquipSlot to the string key used in Glamourer's state JObject.</summary>
    public static string ToSlotName(EquipSlot slot) => slot switch
    {
        EquipSlot.MainHand  => "MainHand",
        EquipSlot.OffHand   => "OffHand",
        EquipSlot.Head      => "Head",
        EquipSlot.Body      => "Body",
        EquipSlot.Hands     => "Hands",
        EquipSlot.Legs      => "Legs",
        EquipSlot.Feet      => "Feet",
        EquipSlot.Ears      => "Ears",
        EquipSlot.Neck      => "Neck",
        EquipSlot.Wrists    => "Wrists",
        EquipSlot.RingRight => "RFinger",
        EquipSlot.RingLeft  => "LFinger",
        _                   => string.Empty,
    };

    private static byte ToApiEquipSlot(EquipSlot slot) => slot switch
    {
        EquipSlot.MainHand  => 1,
        EquipSlot.OffHand   => 2,
        EquipSlot.Head      => 3,
        EquipSlot.Body      => 4,
        EquipSlot.Hands     => 5,
        EquipSlot.Legs      => 7,
        EquipSlot.Feet      => 8,
        EquipSlot.Ears      => 9,
        EquipSlot.Neck      => 10,
        EquipSlot.Wrists    => 11,
        EquipSlot.RingRight => 12,
        EquipSlot.RingLeft  => 14,
        _                   => 0,
    };

    public void Dispose() { }
}
