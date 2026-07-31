using System;
using System.Collections.Generic;
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

    private const ulong WeaponStateFlag = 0x08uL;

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
    }

    // ── Public API ────────────────────────────────────────────────────────────

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
    public bool SetItem(EquipSlot slot, ulong itemId)
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
            var ec = _setItem.InvokeFunc(PlayerIndex, apiSlot, itemId, NoStains, 0u, 0uL);
            _log.Debug($"[Wardrobe] Glamourer SetItem slot={slot}(api={apiSlot}) itemId={itemId} → ec={ec}");
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
