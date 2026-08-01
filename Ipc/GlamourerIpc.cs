using System;
using System.Collections.Generic;
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

    private const ulong WeaponStateFlag   = 0x08uL;
    private const ulong CustomizationFlag = 0x04uL;

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
    public bool ApplyDesignCustomization(Guid designId)
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            var ec = _applyDesign.InvokeFunc(designId, PlayerIndex, 0u, CustomizationFlag);
            _log.Debug($"[Wardrobe] ApplyDesign (customisation only) {designId} → ec={ec}");
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
