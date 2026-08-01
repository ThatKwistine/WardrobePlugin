using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

public class WardrobeService : IDisposable
{
    private readonly PenumbraIpc   _penumbra;
    private readonly GlamourerIpc  _glamourer;
    private readonly Configuration _config;
    private readonly IPluginLog    _log;
    private readonly IFramework    _framework;

    public event Action? WardrobeChanged;

    public WardrobeService(PenumbraIpc penumbra, GlamourerIpc glamourer,
        Configuration config, IPluginLog log, IFramework framework)
    {
        _penumbra  = penumbra;
        _glamourer = glamourer;
        _config    = config;
        _log       = log;
        _framework = framework;

        _penumbra.GameObjectRedrawn += OnPlayerRedrawn;
    }

    public void Dispose()
    {
        _penumbra.GameObjectRedrawn -= OnPlayerRedrawn;
    }

    /// <summary>
    /// Re-applies Glamourer items after any Penumbra redraw that might have reset visual state.
    /// </summary>
    private void OnPlayerRedrawn(int objectIndex)
    {
        if (_config.WornItems.Count == 0) return;

        foreach (var itemId in _config.WornItems.Values.ToList())
        {
            var item = _config.WardrobeItems.Find(x => x.Id == itemId);
            if (item?.GlamourerItemId.HasValue == true)
                _glamourer.SetItem(item.Slot, item.GlamourerItemId.Value);
        }
    }

    /// <summary>
    /// Schedules repeated Glamourer re-applies on the framework thread to handle Penumbra's
    /// async resource reload completing after the initial SetItem call.
    /// Stops early if the item is unequipped between retries.
    /// </summary>
    private void ScheduleGlamourerReapply(EquipSlot slot, ulong glamId, Guid itemId)
    {
        _ = Task.Run(async () =>
        {
            // Delays chosen to cover typical Penumbra reload times (usually <500ms)
            // and slow reloads caused by missing materials (up to ~4s in logs).
            foreach (var delayMs in new[] { 350, 750, 1800, 4500 })
            {
                await Task.Delay(delayMs);
                if (!_config.WornItems.TryGetValue(slot.ToString(), out var curId) || curId != itemId)
                    return; // item was unequipped in the meantime
                await _framework.RunOnFrameworkThread(() => _glamourer.SetItem(slot, glamId));
            }
        });
    }

    // ── Wardrobe items ────────────────────────────────────────────────────────

    public bool WearItem(WardrobeItem item)
    {
        var slotKey = item.Slot.ToString();
        _log.Debug($"[Wardrobe] WearItem '{item.Name}' slot={slotKey} glamItemId={item.GlamourerItemId?.ToString() ?? "null"} ({item.GlamourerItemName ?? "no name"})");

        // Mods split across collections is nearly always a misconfiguration. Each mod is enabled
        // successfully in its own collection, so nothing looks wrong in the log, but only the
        // collection the character actually uses has any visible effect.
        var collections = item.Mods
            .Where(m => !string.IsNullOrEmpty(m.ModDirectory))
            .Select(m => m.Collection)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (collections.Count > 1)
            _log.Warning($"[Wardrobe] '{item.Name}' has mods spread across {collections.Count} collections " +
                         $"({string.Join(", ", collections)}) — only those in the collection your character " +
                         $"uses will take effect. Edit the item to put them in the same collection.");

        // Unequip the previous item in this slot, if any
        if (_config.WornItems.TryGetValue(slotKey, out var prevId) && prevId != item.Id)
        {
            var prev = _config.WardrobeItems.Find(x => x.Id == prevId);
            if (prev != null) UnwearItem(prev, save: false);
        }

        // Set Glamourer BEFORE enabling Penumbra mods.
        // When SetModEnabled fires, Penumbra starts an async resource reload and Glamourer
        // snapshots its current state to re-apply when the reload completes.
        // Calling SetItem first ensures our item choice is in that snapshot, so the
        // post-reload re-apply shows the correct item instead of Emperor's New.
        if (item.GlamourerItemId.HasValue)
        {
            _log.Debug($"[Wardrobe]   Calling Glamourer.SetItem (pre-enable) slot={item.Slot} itemId={item.GlamourerItemId.Value} name='{item.GlamourerItemName}'");
            if (!_glamourer.SetItem(item.Slot, item.GlamourerItemId.Value))
                _log.Warning($"[Wardrobe] Glamourer SetItem returned non-zero for '{item.Name}'");
            else
                _log.Debug($"[Wardrobe]   Glamourer.SetItem (pre-enable) succeeded");
        }

        var success = true;
        var anyNewlyEnabled = false;
        foreach (var mod in item.Mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory)) continue;

            // Enable the mod BEFORE applying options.
            // Penumbra captures the mod's option state at the moment the reload fires (when
            // SetModEnabled is called). Setting options on a disabled mod stores them in config
            // but the initial enable-reload ignores them. Enabling first, then changing options,
            // causes Penumbra to fire a second reload that reads the correct option state.
            var alreadyOn = _penumbra.IsModEnabled(mod.Collection, mod.ModDirectory, mod.ModName);
            if (!alreadyOn)
            {
                var enabled = _penumbra.SetModEnabled(mod.Collection, mod.ModDirectory, mod.ModName, true);
                _log.Debug($"[Wardrobe] SetModEnabled '{mod.ModName}' → {enabled}");
                if (!enabled)
                {
                    _log.Warning($"[Wardrobe] Failed to enable mod '{mod.ModName}' for item '{item.Name}'");
                    success = false;
                }
                else
                {
                    anyNewlyEnabled = true;
                }
            }
            else
            {
                _log.Debug($"[Wardrobe] Mod '{mod.ModName}' already enabled");
            }

            // Apply options on the now-enabled mod. If any option value changes (returns Success
            // rather than NothingChanged), Penumbra automatically fires a new reload that picks
            // up the correct options from the start.
            if (mod.Options.Count > 0)
                _penumbra.ApplyModSettings(mod.Collection, mod.ModDirectory, mod.ModName, mod.Options);
            if (mod.MultiOptions.Count > 0)
                _penumbra.ApplyMultiModSettings(mod.Collection, mod.ModDirectory, mod.ModName, mod.MultiOptions);
        }

        if (success)
            _config.WornItems[slotKey] = item.Id;

        // Secondary SetItem after all mods are enabled — catches cases where the
        // first pre-enable call was overridden by UnwearItem of the previous slot item.
        if (item.GlamourerItemId.HasValue)
        {
            _log.Debug($"[Wardrobe]   Calling Glamourer.SetItem (post-enable) slot={item.Slot} itemId={item.GlamourerItemId.Value}");
            if (!_glamourer.SetItem(item.Slot, item.GlamourerItemId.Value))
                _log.Warning($"[Wardrobe] Glamourer SetItem (post-enable) returned non-zero for '{item.Name}'");
            else
                _log.Debug($"[Wardrobe]   Glamourer.SetItem succeeded");
        }
        else if (!item.Slot.IsCustomization())
        {
            _log.Warning($"[Wardrobe] Item '{item.Name}' has no GlamourerItemId — re-import it to enable auto Glamourer apply");
        }
        else
        {
            // Hair, face, tail and similar replace the character model itself. There is no item to
            // equip — enabling the Penumbra mod above is the entire effect.
            _log.Debug($"[Wardrobe] '{item.Name}' is a {item.Slot.DisplayName()} mod — no Glamourer item to apply");
        }

        ApplyHairstyleFor(item);

        // Penumbra's async resource reload (triggered by SetModEnabled) completes 300ms-4s later
        // and causes Glamourer to re-apply its prior design state, undoing our SetItem call.
        // Schedule repeated re-applies on the framework thread to win that race.
        if (anyNewlyEnabled && item.GlamourerItemId.HasValue)
            ScheduleGlamourerReapply(item.Slot, item.GlamourerItemId.Value, item.Id);

        _config.Save();
        WardrobeChanged?.Invoke();
        return success;
    }

    /// <param name="redraw">
    /// Force a Penumbra redraw when nothing else will refresh the character. Callers that unwear
    /// several items, or immediately wear another, pass false and redraw once themselves.
    /// </param>
    /// <summary>
    /// Switches the character to the hairstyle a hair mod replaces, remembering the previous one.
    /// </summary>
    /// <remarks>
    /// A hair mod only replaces one specific hairstyle's files, so it is invisible unless the
    /// character is actually wearing that hairstyle. The original is stored so reverting restores
    /// it, and is only captured on the first hair item applied — otherwise swapping between two
    /// hair items would overwrite it with the other mod's number.
    /// </remarks>
    private void ApplyHairstyleFor(WardrobeItem item)
    {
        if (item.Slot != EquipSlot.Hair) return;

        if (!_config.ApplyHairstyleWithHairMods)
        {
            _log.Debug($"[Wardrobe] '{item.Name}': hairstyle switching is disabled in settings");
            return;
        }

        // Prefer the number for this character's race — a hair mod is commonly 151 on most races
        // but a different number on Hrothgar and Viera, so the first one found is often wrong.
        ushort? hairstyle = null;
        var raceCode = _glamourer.GetPlayerRaceCode();
        if (raceCode.HasValue && item.HairIdByRace.TryGetValue(raceCode.Value.ToString("D4"), out var perRace))
        {
            hairstyle = perRace;
            _log.Debug($"[Wardrobe] '{item.Name}': hairstyle {perRace} for race code {raceCode:D4}");
        }
        else if (item.HairIdByRace.Count > 0 && raceCode.HasValue)
        {
            _log.Warning($"[Wardrobe] '{item.Name}' does not cover race code {raceCode:D4} — " +
                         $"it supports {string.Join(", ", item.HairIdByRace.Keys)}. Not switching hairstyle.");
            return;
        }
        else
        {
            hairstyle = item.ModelSetId; // older items, or a mod covering a single race
        }

        if (hairstyle is not { } target || target == 0)
        {
            _log.Warning($"[Wardrobe] '{item.Name}' has no hairstyle number stored — click Re-detect " +
                         $"in its edit panel, or re-import it. Items added before hairstyle " +
                         $"switching existed do not have one.");
            return;
        }

        // Only worth capturing when there is no revert design to fall back on
        if (_config.RevertDesignId == null && _config.HairstyleBeforeWardrobe == null)
        {
            var current = _glamourer.GetHairstyle();
            if (current.HasValue && current.Value != target)
            {
                _config.HairstyleBeforeWardrobe = current.Value;
                _log.Debug($"[Wardrobe] Remembering hairstyle {current.Value} before applying '{item.Name}'");
            }
        }

        if (_glamourer.SetHairstyle(target))
            _log.Debug($"[Wardrobe] '{item.Name}' set hairstyle to {target}");
        else
            _log.Warning($"[Wardrobe] '{item.Name}' could not set hairstyle {target} — " +
                         $"the mod will only show if that hairstyle is already selected.");
    }

    /// <summary>
    /// Returns the character's appearance to normal after a customisation mod is reverted.
    /// </summary>
    /// <remarks>
    /// Prefers a Glamourer design nominated in settings, applied with customisations only so the
    /// design's equipment is ignored and whatever the wardrobe has equipped is left alone. That
    /// restores every customisation the mod may have touched, not just the hairstyle.
    /// Falls back to putting back the single hairstyle number captured on apply.
    /// </remarks>
    private void RestoreCustomizationFor(WardrobeItem item)
    {
        if (!item.Slot.IsCustomization()) return;

        // Only restore once the last item in this slot is gone, so swapping between two
        // customisation mods does not bounce the character back in between
        var slotKey = item.Slot.ToString();
        if (_config.WornItems.TryGetValue(slotKey, out var stillWorn) && stillWorn != item.Id)
            return;

        if (_config.RevertDesignId is { } designId)
        {
            _log.Debug($"[Wardrobe] Reverting '{item.Name}' — applying design " +
                       $"'{_config.RevertDesignName}' (customisations only)");
            if (_glamourer.ApplyDesignCustomization(designId))
            {
                _config.HairstyleBeforeWardrobe = null;
                return;
            }
            _log.Warning($"[Wardrobe] Could not apply revert design '{_config.RevertDesignName}' — " +
                         $"it may have been deleted in Glamourer.");
        }

        if (_config.HairstyleBeforeWardrobe is not { } previous) return;

        _log.Debug($"[Wardrobe] Restoring hairstyle {previous} after reverting '{item.Name}'");
        _glamourer.SetHairstyle(previous);
        _config.HairstyleBeforeWardrobe = null;
    }

    public void UnwearItem(WardrobeItem item, bool save = true, bool redraw = true)
    {
        var disabledAny = false;

        foreach (var mod in item.Mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory)) continue;

            // Don't disable the mod if another currently-worn item still needs it
            var stillNeeded = _config.WardrobeItems.Any(other =>
                other.Id != item.Id &&
                _config.WornItems.ContainsValue(other.Id) &&
                other.Mods.Any(m =>
                    m.ModDirectory == mod.ModDirectory &&
                    string.Equals(m.Collection, mod.Collection, StringComparison.OrdinalIgnoreCase)));

            if (!stillNeeded && _penumbra.SetModEnabled(mod.Collection, mod.ModDirectory, mod.ModName, false))
                disabledAny = true;
        }

        var slotKey = item.Slot.ToString();
        if (_config.WornItems.TryGetValue(slotKey, out var curId) && curId == item.Id)
            _config.WornItems.Remove(slotKey);

        // Set the slot to the Emperor's New item so it appears empty in Glamourer.
        //
        // Only ever revert a slot this item actually set. Without the GlamourerItemId check, a mod
        // with no detected game item — a piercing, a tattoo, anything the user had to assign a slot
        // to by hand — would empty a slot it never filled, removing real gear the user was wearing.
        //
        // Worse, many such mods attach themselves to an Emperor's New item precisely because it is
        // invisible, so "emptying" the slot equips the very item the mod replaces.
        var swappedItem = false;
        if (item.Slot != EquipSlot.Unknown && !item.Slot.IsCustomization() && item.GlamourerItemId.HasValue)
        {
            var emperorsId = ItemLookupService.FindEmperorsNewItem(item.Slot);
            if (emperorsId.HasValue)
            {
                _log.Debug($"[Wardrobe] UnwearItem: setting slot {item.Slot} to Emperor's New (id={emperorsId.Value})");
                _glamourer.SetItem(item.Slot, emperorsId.Value);
                swappedItem = true;
            }
            else
            {
                _log.Warning($"[Wardrobe] UnwearItem: no Emperor's New item found for slot {item.Slot}");
            }
        }

        // Turning a redirection off does not reload anything already on the character. Swapping the
        // Glamourer item normally forces that reload as a side effect, but a mod with no item to
        // swap — hair, skin, or a shared texture like a piercing — would otherwise stay visible
        // until something else redrew the character.
        RestoreCustomizationFor(item);

        if (redraw && disabledAny && !swappedItem)
        {
            _log.Debug($"[Wardrobe] UnwearItem: redrawing for '{item.Name}' — no Glamourer item swap to force a reload");
            _penumbra.RedrawPlayer();
        }

        if (save)
        {
            _config.Save();
            WardrobeChanged?.Invoke();
        }
    }

    public bool IsItemWorn(WardrobeItem item) =>
        _config.WornItems.TryGetValue(item.Slot.ToString(), out var id) && id == item.Id;

    /// <summary>
    /// Disables all tracked mods, clears WornItems, then forces every equipment slot in Glamourer
    /// to its Emperor's New item so the character appears fully unequipped.
    /// </summary>
    public void StripAll()
    {
        foreach (var id in _config.WornItems.Values.ToList())
        {
            var item = _config.WardrobeItems.Find(x => x.Id == id);
            if (item != null) UnwearItem(item, save: false);
        }
        _config.WornItems.Clear();

        // Force every equipment slot to Emperor's New regardless of what Glamourer currently has.
        // Customisation slots are skipped — stripping cannot remove a character's hair.
        foreach (var slot in EquipSlotEx.All)
        {
            if (slot.IsCustomization()) continue;
            var emperorsId = ItemLookupService.FindEmperorsNewItem(slot);
            if (emperorsId.HasValue)
                _glamourer.SetItem(slot, emperorsId.Value);
        }

        _config.Save();
        WardrobeChanged?.Invoke();
    }

    /// <summary>
    /// Checks which wardrobe items are currently worn by cross-referencing:
    ///   1. All required mods are enabled in Penumbra
    ///   2. Active mod options match the stored options (if any)
    ///   3. Glamourer has the expected item equipped in the correct slot (if available)
    /// Updates WornItems for any newly detected items (first match wins per slot).
    /// Returns the IDs that were newly added to WornItems.
    /// </summary>
    public HashSet<Guid> ScanAndSyncWorn()
    {
        var added = new HashSet<Guid>();

        // Fetch Glamourer's current equipment state once for the whole scan.
        // Keys are Glamourer slot names (Head, Body, RFinger, …); values are item row IDs.
        var glamEquipment = _glamourer.GetAllEquipment();
        _log.Debug($"[Wardrobe] Scan: Glamourer equipment snapshot has {glamEquipment?.Count ?? -1} slots");

        foreach (var item in _config.WardrobeItems)
        {
            if (item.Mods.Count == 0 || item.Slot == EquipSlot.Unknown) continue;

            // Check 1: all mods are enabled in Penumbra
            var disabledMods = item.Mods
                .Where(m => string.IsNullOrEmpty(m.ModDirectory) ||
                            !_penumbra.IsModEnabled(m.Collection, m.ModDirectory, m.ModName))
                .Select(m => string.IsNullOrEmpty(m.ModDirectory) ? $"{m.ModName} (no dir)" : m.ModName)
                .ToList();
            if (disabledMods.Count > 0)
            {
                _log.Debug($"[Wardrobe] Scan: '{item.Name}' skipped — mods not enabled in Penumbra: {string.Join(", ", disabledMods)}");
                continue;
            }

            // Check 2: mod options must match, single- and multi-select alike.
            // Multi-select was previously excluded because it never matched — that was the
            // TrySetModSetting/TrySetModSettings bug (see PenumbraIpc.ApplyMultiModSettings),
            // not an inherent limitation. Now that the apply path is correct, matching on
            // multi-select again rules out items that share a mod but want different checkboxes.
            var failedMods = item.Mods
                .Where(m => !_penumbra.ActiveOptionsMatch(m.Collection, m.ModDirectory, m.ModName, m.Options, m.MultiOptions))
                .Select(m => m.ModName)
                .ToList();
            if (failedMods.Count > 0)
            {
                _log.Debug($"[Wardrobe] Scan: '{item.Name}' skipped — options mismatch for: {string.Join(", ", failedMods)}");
                continue;
            }

            // Check 3: if Glamourer state is available and the item has a known item ID,
            // require the Glamourer slot to contain exactly that item.
            // This rules out false positives when multiple wardrobe items share the same mod
            // but only one is actually equipped in Glamourer.
            if (item.GlamourerItemId.HasValue && glamEquipment != null)
            {
                var slotName = GlamourerIpc.ToSlotName(item.Slot);
                if (!string.IsNullOrEmpty(slotName) &&
                    glamEquipment.TryGetValue(slotName, out var equippedId) &&
                    equippedId != item.GlamourerItemId.Value)
                {
                    _log.Debug($"[Wardrobe] Scan: '{item.Name}' skipped — Glamourer {slotName} " +
                               $"has {equippedId}, expected {item.GlamourerItemId.Value}");
                    continue;
                }
            }

            var slotKey = item.Slot.ToString();
            if (!_config.WornItems.ContainsKey(slotKey))
            {
                _config.WornItems[slotKey] = item.Id;
                added.Add(item.Id);
                _log.Debug($"[Wardrobe] Scan: detected '{item.Name}' as worn");
            }
        }

        if (added.Count > 0) _config.Save();
        return added;
    }

    // ── Legacy outfit system ──────────────────────────────────────────────────

    public bool WearOutfit(Outfit outfit)
    {
        if (_config.CurrentlyWornOutfitId.HasValue &&
            _config.CurrentlyWornOutfitId.Value != outfit.Id)
            UnequipCurrentOutfit(revertGlamourer: false);

        var success = true;

        if (!string.IsNullOrEmpty(outfit.PenumbraModDirectory))
        {
            _penumbra.ApplyModSettings(outfit.PenumbraCollection, outfit.PenumbraModDirectory,
                outfit.Name, outfit.ModSettings);

            if (!_penumbra.SetModEnabled(outfit.PenumbraCollection, outfit.PenumbraModDirectory,
                outfit.Name, outfit.ModEnabled))
            {
                _log.Warning($"[Wardrobe] Failed to set mod state for outfit '{outfit.Name}'");
                success = false;
            }
        }

        if (!string.IsNullOrEmpty(outfit.GlamourerStateBase64))
        {
            if (!_glamourer.ApplyState(outfit.GlamourerStateBase64))
            {
                _log.Warning($"[Wardrobe] Failed to apply Glamourer state for '{outfit.Name}'");
                success = false;
            }
        }

        outfit.IsCurrentlyWorn        = success;
        _config.CurrentlyWornOutfitId = success ? outfit.Id : null;
        _config.Save();
        return success;
    }

    public void UnequipCurrentOutfit(bool revertGlamourer = true)
    {
        if (!_config.CurrentlyWornOutfitId.HasValue) return;
        var worn = _config.Outfits.Find(o => o.Id == _config.CurrentlyWornOutfitId.Value);
        if (worn != null) UnequipOutfit(worn, revertGlamourer);
        _config.CurrentlyWornOutfitId = null;
        _config.Save();
    }

    public void UnequipOutfit(Outfit outfit, bool revertGlamourer = true)
    {
        if (!string.IsNullOrEmpty(outfit.PenumbraModDirectory) && outfit.ModEnabled)
            _penumbra.SetModEnabled(outfit.PenumbraCollection, outfit.PenumbraModDirectory, outfit.Name, false);

        if (revertGlamourer)
            _glamourer.RevertState();

        outfit.IsCurrentlyWorn = false;
    }

    public bool CaptureGlamourerState(Outfit outfit)
    {
        var state = _glamourer.CaptureCurrentState();
        if (state == null) return false;
        outfit.GlamourerStateBase64 = state;
        return true;
    }

    public bool CaptureModSettings(Outfit outfit)
    {
        if (string.IsNullOrEmpty(outfit.PenumbraModDirectory)) return false;
        outfit.ModSettings = _penumbra.GetModSettings(
            outfit.PenumbraCollection, outfit.PenumbraModDirectory, outfit.Name);
        return true;
    }
}
