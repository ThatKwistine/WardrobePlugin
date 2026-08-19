using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using WardrobePlugin.Ipc;
using WardrobePlugin.Models;
using WardrobePlugin.Ui;

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

        // Re-apply the active outfit's dyes as well, or a redraw would strip them back to undyed
        var outfit = _activeOutfitId is { } activeId
            ? _config.Outfits.Find(o => o.Id == activeId)
            : null;

        // Advanced dye rows are gathered rather than applied per item: each apply is a whole state
        // round trip, so one for the character beats one for every piece it is wearing
        var advanced = new Dictionary<string, string>();
        var keepAdvanced = _config.AdvancedDyesEnabled;

        foreach (var itemId in _config.WornItems.Values.ToList())
        {
            var item = _config.WardrobeItems.Find(x => x.Id == itemId);
            if (item?.GlamourerItemId is not { } glamId) continue;

            var dye = outfit != null ? GetDye(outfit, item.Id) : null;
            _glamourer.SetItem(item.Slot, glamId, dye?.Stain1 ?? 0, dye?.Stain2 ?? 0);

            if (dye == null || !keepAdvanced) continue;
            foreach (var (key, row) in dye.Advanced)
                advanced[key] = row;
        }

        // A redraw resets the colour tables along with everything else, so these need putting back
        // for the same reason the stains above do
        if (advanced.Count > 0)
            _glamourer.ApplyAdvancedDyes(advanced);
    }

    /// <summary>
    /// Schedules repeated Glamourer re-applies on the framework thread to handle Penumbra's
    /// async resource reload completing after the initial SetItem call.
    /// Stops early if the item is unequipped between retries.
    /// </summary>
    private void ScheduleGlamourerReapply(EquipSlot slot, string wornKey, ulong glamId, Guid itemId,
        byte stain1, byte stain2, IReadOnlyDictionary<string, string>? advanced = null)
    {
        _ = Task.Run(async () =>
        {
            // Delays chosen to cover typical Penumbra reload times (usually <500ms)
            // and slow reloads caused by missing materials (up to ~4s in logs).
            foreach (var delayMs in new[] { 350, 750, 1800, 4500 })
            {
                await Task.Delay(delayMs);
                if (!_config.WornItems.TryGetValue(wornKey, out var curId) || curId != itemId)
                    return; // item was unequipped in the meantime

                await _framework.RunOnFrameworkThread(() =>
                {
                    _glamourer.SetItem(slot, glamId, stain1, stain2);

                    // After the item, always: a colour row describes the material of whatever is in
                    // the slot, so applying it before the reload lands would dye the outgoing piece
                    if (advanced is { Count: > 0 })
                        _glamourer.ApplyAdvancedDyes(advanced);
                });
            }
        });
    }

    // ── Mod ownership ─────────────────────────────────────────────────────────

    /// <summary>Key identifying a mod in <see cref="Configuration.ModsEnabledByWardrobe"/>.</summary>
    /// <remarks>
    /// Collection and directory together, matching how <see cref="UnwearItem"/> decides a mod is
    /// still needed. Mod *name* is deliberately left out: it is display text that changes when a
    /// mod is renamed in Penumbra, which would silently orphan the claim.
    /// </remarks>
    private static string ModKey(ModReference mod) =>
        $"{mod.Collection}|{mod.ModDirectory}".ToLowerInvariant();

    /// <summary>
    /// Records that the wardrobe, rather than the user, switched a mod on.
    /// </summary>
    private void ClaimMod(ModReference mod)
    {
        if (_config.ModsEnabledByWardrobe.Add(ModKey(mod)))
            _log.Debug($"[Wardrobe] Claimed '{mod.ModName}' — the wardrobe enabled it, so the wardrobe may disable it");
    }

    /// <summary>
    /// Whether the wardrobe enabled this mod and may therefore turn it off again.
    /// </summary>
    /// <remarks>
    /// A mod that was already on when the item was worn is left alone: it is on for a reason the
    /// wardrobe cannot see — a Glamourer design, or the user's own choice in Penumbra — and turning
    /// it off would break something that has nothing to do with the item being removed.
    /// </remarks>
    private bool OwnsMod(ModReference mod) =>
        _config.ModsEnabledByWardrobe.Contains(ModKey(mod));

    private void ReleaseMod(ModReference mod) =>
        _config.ModsEnabledByWardrobe.Remove(ModKey(mod));

    // ── Wardrobe items ────────────────────────────────────────────────────────

    public bool WearItem(WardrobeItem item, OutfitDye? dye = null)
    {
        var slotKey = item.WornKey();
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

        // Whether this ends in a redraw, decided up front because the removal below has to know:
        // swapping one hair mod for another would otherwise redraw twice, once on the way out and
        // again on the way in, for a single click
        var forceRedraw = item.ForcesRedraw();

        // Unequip the previous item in this slot, if any
        if (_config.WornItems.TryGetValue(slotKey, out var prevId) && prevId != item.Id)
        {
            var prev = _config.WardrobeItems.Find(x => x.Id == prevId);
            if (prev != null) UnwearItem(prev, save: false, redraw: !forceRedraw, restoreBase: false);
        }

        // Set Glamourer BEFORE enabling Penumbra mods.
        // When SetModEnabled fires, Penumbra starts an async resource reload and Glamourer
        // snapshots its current state to re-apply when the reload completes.
        // Calling SetItem first ensures our item choice is in that snapshot, so the
        // post-reload re-apply shows the correct item instead of Emperor's New.
        var stain1 = dye?.Stain1 ?? 0;
        var stain2 = dye?.Stain2 ?? 0;

        if (item.GlamourerItemId.HasValue)
        {
            _log.Debug($"[Wardrobe]   Calling Glamourer.SetItem (pre-enable) slot={item.Slot} itemId={item.GlamourerItemId.Value} name='{item.GlamourerItemName}'");
            if (!_glamourer.SetItem(item.Slot, item.GlamourerItemId.Value, stain1, stain2))
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
                    ClaimMod(mod);
                }
            }
            else
            {
                // Deliberately unclaimed — see OwnsMod
                _log.Debug($"[Wardrobe] Mod '{mod.ModName}' already enabled — leaving it to whoever turned it on");
            }

            // Apply options on the now-enabled mod. If any option value changes (returns Success
            // rather than NothingChanged), Penumbra automatically fires a new reload that picks
            // up the correct options from the start.
            if (mod.Options.Count > 0)
                _penumbra.ApplyModSettings(mod.Collection, mod.ModDirectory, mod.ModName, mod.Options);

            // Tri-states win where an item has them, and the whole-selection field is the fallback
            // for items saved before they existed — never both, or the fallback would re-flatten
            // exactly the groups the tri-states were asked to leave alone
            if (mod.OptionStates.Count > 0)
                _penumbra.ApplyModOptionStates(mod.Collection, mod.ModDirectory, mod.ModName, mod.OptionStates);
            else if (mod.MultiOptions.Count > 0)
                _penumbra.ApplyMultiModSettings(mod.Collection, mod.ModDirectory, mod.ModName, mod.MultiOptions);
        }

        if (success)
            _config.WornItems[slotKey] = item.Id;

        // Secondary SetItem after all mods are enabled — catches cases where the
        // first pre-enable call was overridden by UnwearItem of the previous slot item.
        if (item.GlamourerItemId.HasValue)
        {
            _log.Debug($"[Wardrobe]   Calling Glamourer.SetItem (post-enable) slot={item.Slot} itemId={item.GlamourerItemId.Value}");
            if (!_glamourer.SetItem(item.Slot, item.GlamourerItemId.Value, stain1, stain2))
                _log.Warning($"[Wardrobe] Glamourer SetItem (post-enable) returned non-zero for '{item.Name}'");
            else
                _log.Debug($"[Wardrobe]   Glamourer.SetItem succeeded");
        }
        else if (!item.Slot.IsModOnly())
        {
            _log.Warning($"[Wardrobe] Item '{item.Name}' has no GlamourerItemId — re-import it to enable auto Glamourer apply");
        }
        else
        {
            // Hair, face and similar replace the character model itself; emotes, VFX and mounts
            // are not worn at all. Either way there is no item to equip — enabling the Penumbra
            // mod above is the entire effect.
            _log.Debug($"[Wardrobe] '{item.Name}' is a {item.Slot.DisplayName()} mod — no Glamourer item to apply");
        }

        ApplyHairstyleFor(item);

        // Enabling a mod redirects files but reloads nothing already drawn, so a mod with no
        // Glamourer item to swap can go on and not appear until something else redraws the
        // character — which is why hair and face mods so often "did nothing" until Penumbra was
        // told to redraw by hand (#16). Before the dye rows below: a redraw resets the colour
        // tables, so putting them back afterwards is what makes them stick.
        if (forceRedraw)
        {
            _log.Debug($"[Wardrobe] WearItem: redrawing for '{item.Name}' — the item asks for one");
            _penumbra.RedrawPlayer();
        }

        // Advanced dyes go on after the piece, never before: the rows address the material of
        // whatever occupies the slot, so applying them to the outgoing item would colour that
        var advanced = _config.AdvancedDyesEnabled ? dye?.Advanced : null;
        if (advanced is { Count: > 0 })
            _glamourer.ApplyAdvancedDyes(advanced);

        // Penumbra's async resource reload (triggered by SetModEnabled) completes 300ms-4s later
        // and causes Glamourer to re-apply its prior design state, undoing our SetItem call.
        // Schedule repeated re-applies on the framework thread to win that race.
        if (anyNewlyEnabled && item.GlamourerItemId.HasValue)
            ScheduleGlamourerReapply(item.Slot, slotKey, item.GlamourerItemId.Value, item.Id, stain1, stain2,
                advanced);

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
    /// <summary>
    /// The hairstyle number an item stores for a model race code, or null when it covers no such race.
    /// </summary>
    /// <remarks>
    /// Compares the keys as numbers rather than as text, because the two halves disagreed about
    /// padding and nothing caught it: every import writes the key with <c>int.ToString()</c>, giving
    /// <c>"401"</c>, while the lookup asked for <c>"D4"</c>, giving <c>"0401"</c>. Every race code
    /// below 1000 therefore missed — all Hyur, Elezen and Miqo'te, and male Roegadyn — and the
    /// warning said the mod did not cover a race whose number it was plainly listing. Only the
    /// four-digit races ever worked.
    /// <para>
    /// Fixed on the reading side on purpose. Repadding the keys at import would leave every item
    /// already saved still broken and need a migration to match; comparing numerically fixes those
    /// items where they sit, and cannot be undone by a future writer choosing a different format.
    /// The dictionary holds one entry per race the mod covers, so the scan is nothing.
    /// </para>
    /// </remarks>
    private static ushort? HairIdForRace(WardrobeItem item, int raceCode)
    {
        foreach (var (key, id) in item.HairIdByRace)
            if (int.TryParse(key, out var code) && code == raceCode)
                return id;

        return null;
    }

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
        if (raceCode.HasValue && HairIdForRace(item, raceCode.Value) is { } perRace)
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
        var slotKey = item.WornKey();
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

    /// <returns>
    /// True when the character still needs a redraw for this removal to be visible and none was
    /// done — either because <paramref name="redraw"/> was false, or because there was nothing to
    /// redraw for. Lets a caller removing several items at once redraw only if one of them actually
    /// asked for it, instead of forcing one on a set that swapped its Glamourer items and did not
    /// need it.
    /// </returns>
    /// <param name="restoreBase">
    /// Whether the base character is put back over the slot this item vacates. True for a removal on
    /// its own, which is the whole of what a base character promises: a base hair displaced by a hair
    /// mod has to come back when that mod comes off, or taking the mod off leaves the character in
    /// their vanilla hair rather than in themselves. False for a removal that is one step of a larger
    /// sequence — the caller either re-applies the base itself once at the end, or is about to fill
    /// the slot with something else and would only be fighting it.
    /// </param>
    public bool UnwearItem(WardrobeItem item, bool save = true, bool redraw = true, bool restoreBase = true)
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

            if (stillNeeded) continue;

            // Only ever switch off what the wardrobe switched on
            if (!OwnsMod(mod))
            {
                _log.Debug($"[Wardrobe] Leaving '{mod.ModName}' enabled — the wardrobe did not turn it on");
                continue;
            }

            if (_penumbra.SetModEnabled(mod.Collection, mod.ModDirectory, mod.ModName, false))
            {
                ReleaseMod(mod);
                disabledAny = true;
            }
        }

        var slotKey = item.WornKey();
        if (_config.WornItems.TryGetValue(slotKey, out var curId) && curId == item.Id)
            _config.WornItems.Remove(slotKey);

        // Advanced dyes are keyed by slot, not by item, so leaving them behind would hand this
        // piece's colours to whatever is worn here next. Put them back before the slot is emptied.
        var activeOutfit = _activeOutfitId is { } outfitId
            ? _config.Outfits.Find(o => o.Id == outfitId)
            : null;

        if (_config.AdvancedDyesEnabled && activeOutfit != null &&
            GetDye(activeOutfit, item.Id)?.Advanced is { Count: > 0 } worn)
            _glamourer.RevertAdvancedDyes(worn);

        // Set the slot to the Emperor's New item so it appears empty in Glamourer.
        //
        // Only ever revert a slot this item actually set. Without the GlamourerItemId check, a mod
        // with no detected game item — a piercing, a tattoo, anything the user had to assign a slot
        // to by hand — would empty a slot it never filled, removing real gear the user was wearing.
        //
        // Worse, many such mods attach themselves to an Emperor's New item precisely because it is
        // invisible, so "emptying" the slot equips the very item the mod replaces.
        var swappedItem = false;
        if (item.Slot != EquipSlot.Unknown && !item.Slot.IsModOnly() && item.GlamourerItemId.HasValue)
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

        // An item set to force one asks on the way out as well, whether or not a mod was switched
        // off for it: the mod may have been left enabled because the wardrobe did not turn it on,
        // and the character still has to be reloaded for the slot to look like itself again.
        var needsRedraw = item.ForcesRedraw() || (disabledAny && !swappedItem);
        if (redraw && needsRedraw)
        {
            _log.Debug($"[Wardrobe] UnwearItem: redrawing for '{item.Name}' — " +
                       (item.ForcesRedraw() ? "the item asks for one" : "no Glamourer item swap to force a reload"));
            _penumbra.RedrawPlayer();
            needsRedraw = false;
        }

        // After the slot has been emptied, so the base's own item is put into a slot that is free
        // rather than displaced straight back out again. ApplyBase only wears what is not already
        // worn, so this costs nothing when the item removed was never covering the base.
        //
        // Never for one of the base's own items, though: someone taking a base item off by hand has
        // said what they want, and putting it straight back would make the button appear broken. The
        // base returns on the next strip, which is the moment it is meant to.
        if (restoreBase && _config.ActiveBaseCharacter?.ItemIds.Contains(item.Id) != true)
            ApplyBase();

        if (save)
        {
            _config.Save();
            WardrobeChanged?.Invoke();
        }

        return needsRedraw;
    }

    public bool IsItemWorn(WardrobeItem item) =>
        _config.WornItems.TryGetValue(item.WornKey(), out var id) && id == item.Id;

    // ── Linked items ──────────────────────────────────────────────────────────

    /// <summary>
    /// The items linked to this one that still exist, in slot order.
    /// </summary>
    /// <remarks>
    /// One hop: the partners' own links are not followed. Ids left behind by deleted items are
    /// skipped rather than repaired here — <see cref="ForgetLinksTo"/> does the tidying, and a read
    /// during drawing must not write to the config.
    /// </remarks>
    public List<WardrobeItem> ResolveLinks(WardrobeItem item) =>
        item.LinkedItemIds
            .Select(id => _config.WardrobeItems.Find(x => x.Id == id))
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => (int)x.Slot)
            .ToList();

    /// <summary>Why two items cannot be linked, or null when they can.</summary>
    /// <remarks>
    /// Two items that occupy the same key displace each other: wearing the pair would leave whichever
    /// was applied last on the character and the other recorded as not worn, so the link would read
    /// as broken every time it was used. Refused at the point of linking, where it can be explained,
    /// rather than silently misbehaving later.
    /// </remarks>
    public static string? LinkRefusal(WardrobeItem a, WardrobeItem b)
    {
        if (a.Id == b.Id) return "An item cannot be linked to itself.";

        if (a.WornKey() == b.WornKey())
            return a.Slot.IsModCategory()
                ? $"Both replace {a.Replaces} — wearing one takes the other off, so linking them would never hold."
                : $"Both are {a.Slot.DisplayName()} items — wearing one takes the other off, so linking them would never hold.";

        return null;
    }

    public static bool AreLinked(WardrobeItem a, WardrobeItem b) =>
        a.LinkedItemIds.Contains(b.Id);

    /// <summary>
    /// Links two items so each wears and removes the other. Returns false when they cannot be
    /// linked, with the reason in <paramref name="refusal"/>.
    /// </summary>
    /// <remarks>
    /// Writes both sides. Does not save — callers link several pairs at once and save when done.
    /// </remarks>
    public bool Link(WardrobeItem a, WardrobeItem b, out string? refusal)
    {
        refusal = LinkRefusal(a, b);
        if (refusal != null) return false;

        if (!a.LinkedItemIds.Contains(b.Id)) a.LinkedItemIds.Add(b.Id);
        if (!b.LinkedItemIds.Contains(a.Id)) b.LinkedItemIds.Add(a.Id);
        return true;
    }

    /// <summary>Breaks the link between two items, from both sides. Does not save.</summary>
    public bool Unlink(WardrobeItem a, WardrobeItem b)
    {
        var removed  = a.LinkedItemIds.Remove(b.Id);
        return b.LinkedItemIds.Remove(a.Id) || removed;
    }

    /// <summary>
    /// Drops every reference to an item from the other half of its links, for use before deleting it.
    /// </summary>
    /// <remarks>
    /// A dangling id is harmless to read past, but left in place it would re-attach itself if that
    /// Guid ever came back — which re-importing an exported item does. Does not save: deletion
    /// already writes the config once it is done removing.
    /// </remarks>
    public void ForgetLinksTo(Guid id)
    {
        foreach (var other in _config.WardrobeItems)
            other.LinkedItemIds.Remove(id);
    }

    // ── Variant groups ────────────────────────────────────────────────────────

    /// <summary>Every variant of this item that still exists, oldest first.</summary>
    public List<WardrobeItem> ResolveVariants(WardrobeItem original) =>
        _config.WardrobeItems
            .Where(i => i.Id != original.Id && i.VariantOfId == original.Id)
            .OrderBy(i => i.DateAdded)
            .ToList();

    /// <summary>The item a variant belongs to, or null when this is not a variant.</summary>
    public WardrobeItem? ResolveOriginal(WardrobeItem item) =>
        item.VariantOfId is { } id ? _config.WardrobeItems.Find(x => x.Id == id) : null;

    /// <summary>
    /// Keeps a variant group together when the item at its head is deleted, by promoting the oldest
    /// remaining variant in its place. Call before removing the item. Does not save.
    /// </summary>
    /// <remarks>
    /// Without this, deleting an original would scatter its variants into separate cards — they all
    /// still hold its id, which now matches nothing. Since every member is the same mods in
    /// different options, any of them can head the group, so the oldest takes over and the fold
    /// state moves with it rather than the group springing open.
    /// </remarks>
    public void ReparentVariantsOf(WardrobeItem original)
    {
        var groupKey = original.Id.ToString();

        // A variant being deleted heads nothing; only its own membership goes, which needs no work
        if (original.VariantOfId.HasValue) return;

        var variants = ResolveVariants(original);
        if (variants.Count == 0)
        {
            _config.ExpandedVariantGroups.Remove(groupKey);
            return;
        }

        var promoted = variants[0];
        promoted.VariantOfId = null;

        foreach (var variant in variants.Skip(1))
            variant.VariantOfId = promoted.Id;

        if (_config.ExpandedVariantGroups.Remove(groupKey))
            _config.ExpandedVariantGroups.Add(promoted.Id.ToString());

        _log.Debug($"[Wardrobe] '{original.Name}' deleted — '{promoted.Name}' now heads its " +
                   $"{variants.Count} variant(s)");
    }

    /// <summary>
    /// The name a new variant of this item would be given, in the configured style.
    /// </summary>
    /// <remarks>
    /// Built from the group's original rather than from whatever was copied, so making a variant of
    /// a variant gives "Silk Top (Variant-3)" and not "Silk Top (Variant-2) (Variant-2)". The number
    /// continues from the variants the original already has, which is what makes the sequence hold
    /// when they are created one at a time over weeks.
    /// </remarks>
    public string NextVariantName(WardrobeItem source)
    {
        var original = ResolveOriginal(source) ?? source;
        return original.Name + VariantSuffix(_config.VariantNameStyle, ResolveVariants(original).Count + 1);
    }

    /// <param name="index">Which variant this is, counting from 1.</param>
    public static string VariantSuffix(VariantNameStyle style, int index) => style switch
    {
        VariantNameStyle.Numbered  => $" (Variant-{index})",
        VariantNameStyle.Lettered  => $" (Variant-{VariantLetter(index)})",

        // Separators are quoted because bare '/' and ':' in a format string are placeholders for
        // whatever the current culture uses, so an unquoted format would render differently
        // depending on the machine's locale rather than matching what the settings preview showed.
        //
        // Minutes, so two variants made in the same minute collide. Shown in the settings preview,
        // where picking this style displays the same name twice over.
        VariantNameStyle.Timestamp => $" ({DateTime.Now:dd'/'MM'/'yy - HH':'mm})",

        _                          => " (variant)",
    };

    /// <summary>1 → A, 26 → Z, 27 → AA, so the sequence does not run out at Z.</summary>
    private static string VariantLetter(int index)
    {
        var letters = new StringBuilder();
        while (index > 0)
        {
            index--;
            letters.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return letters.ToString();
    }

    /// <summary>
    /// Takes an item out of its variant group, or dissolves the group when given its original.
    /// Does not save.
    /// </summary>
    /// <remarks>
    /// The way to undo a grouping that was inferred rather than recorded — items imported before
    /// variants tracked where they came from are grouped by a rule that cannot tell a genuine
    /// variant from two items that merely share a mod and a slot.
    /// </remarks>
    public void DetachFromVariantGroup(WardrobeItem item)
    {
        if (item.VariantOfId.HasValue)
        {
            item.VariantOfId = null;
            return;
        }

        foreach (var variant in ResolveVariants(item))
            variant.VariantOfId = null;

        _config.ExpandedVariantGroups.Remove(item.Id.ToString());
    }

    /// <summary>
    /// Wears an item together with everything linked to it.
    /// </summary>
    /// <remarks>
    /// The item itself goes on first, so a partner that shares a mod finds it already enabled and
    /// claimed, and so the thing actually clicked wins any Glamourer race against the rest.
    /// Partners already worn are skipped rather than re-applied — re-wearing costs a Penumbra reload
    /// and would restart the re-apply retries for something already correct.
    /// Returns false if any of them failed, exactly as <see cref="WearItem"/> does.
    /// </remarks>
    public bool WearItemLinked(WardrobeItem item, OutfitDye? dye = null)
    {
        var success = WearItem(item, dye);

        foreach (var partner in ResolveLinks(item))
        {
            if (IsItemWorn(partner)) continue;
            _log.Debug($"[Wardrobe] '{item.Name}' also wears linked item '{partner.Name}'");
            if (!WearItem(partner)) success = false;
        }

        return success;
    }

    /// <summary>
    /// Removes an item together with any linked items currently worn.
    /// </summary>
    /// <remarks>
    /// One save and at most one redraw for the whole set, as <see cref="UnwearOutfit"/> does: each
    /// removal on its own would write the config repeatedly and could redraw the character several
    /// times over for a single click.
    /// </remarks>
    public void UnwearItemLinked(WardrobeItem item)
    {
        var worn = ResolveLinks(item).Where(IsItemWorn).ToList();

        // Nothing to batch, so hand straight over rather than redraw where the single-item path
        // would not have. A redraw is visible on the character, and one per unequip of an ordinary
        // unlinked item would be a change to every removal in the wardrobe.
        if (worn.Count == 0)
        {
            UnwearItem(item);
            return;
        }

        // Suppressed per item so the set comes off in one go rather than redrawing the character
        // once for each piece. Only a removal that disabled mods without swapping a Glamourer item —
        // hair, an animation, a texture mod — has nothing else to make it visible and asks for one;
        // ordinary gear forces its own reload by swapping to Emperor's New, and redrawing on top of
        // that is a visible stutter for no benefit.
        var needsRedraw = UnwearItem(item, save: false, redraw: false, restoreBase: false);

        foreach (var partner in worn)
        {
            _log.Debug($"[Wardrobe] '{item.Name}' also removes linked item '{partner.Name}'");
            needsRedraw |= UnwearItem(partner, save: false, redraw: false, restoreBase: false);
        }

        if (needsRedraw) _penumbra.RedrawPlayer();

        // Once for the whole set rather than per item, for the same reason the redraw is
        ApplyBase();

        _log.Information($"[Wardrobe] Removed '{item.Name}' with {worn.Count} linked item(s)");

        _config.Save();
        WardrobeChanged?.Invoke();
    }

    // ── Base character ────────────────────────────────────────────────────────

    /// <summary>
    /// The slots a base character holds against a strip: those ticked, plus those its own items
    /// occupy.
    /// </summary>
    /// <remarks>
    /// An item's slot is protected on its behalf rather than needing its own tick. A tail mod worn
    /// on a ring is in the base precisely because it is not really a ring, and asking the user to
    /// also remember which finger it was on would be a second chance to get it wrong.
    /// </remarks>
    public HashSet<EquipSlot> KeptSlots(BaseCharacter? baseChar)
    {
        var slots = new HashSet<EquipSlot>();
        if (baseChar == null) return slots;

        foreach (var name in baseChar.KeepSlots)
            if (Enum.TryParse<EquipSlot>(name, out var slot)) slots.Add(slot);

        foreach (var item in ResolveBase(baseChar))
            slots.Add(item.Slot);

        return slots;
    }

    /// <summary>Items of a base character that still exist, in slot order and each only once.</summary>
    /// <remarks>
    /// <see cref="Configuration.NormaliseBaseCharacters"/> clears duplicates on load and both add
    /// paths refuse them, so <c>Distinct</c> here is the belt to their braces: everything that reads
    /// a base goes through this, and a repeated id would otherwise mean a row drawn twice and an
    /// item applied twice — each apply being a Penumbra reload nobody asked for.
    /// </remarks>
    public List<WardrobeItem> ResolveBase(BaseCharacter baseChar) =>
        baseChar.ItemIds
            .Distinct()
            .Select(id => _config.WardrobeItems.Find(x => x.Id == id))
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => (int)x.Slot)
            .ToList();

    /// <summary>
    /// Puts the active base character back on the character: its design's customisations, then any
    /// of its items that are not already worn.
    /// </summary>
    /// <remarks>
    /// Safe to call repeatedly, which is the point — a screenshot session calls it before every
    /// shot, so a base item displaced by the piece being photographed comes back for the next one
    /// instead of the session quietly drifting away from the character it started with.
    /// <para>
    /// Only the design's customisations are applied unless the base asks for its equipment too. Gear
    /// would otherwise put back the very clothes a strip had just removed; the base's own gear is
    /// normally its items and its kept slots. See
    /// <see cref="BaseCharacter.DesignAppliesEquipment"/> for when that is the wrong default.
    /// </para>
    /// </remarks>
    /// <returns>How many items were newly applied. Zero when everything was already in place.</returns>
    public int ApplyBase(BaseCharacter? baseChar = null)
    {
        baseChar ??= _config.ActiveBaseCharacter;
        if (baseChar == null) return 0;

        if (baseChar.DesignId is { } designId)
        {
            // Before the items below, so a base item always wins the slot it is in — the design is the
            // look underneath, and an item attached to the base is a deliberate override of it
            var applied = baseChar.DesignAppliesEquipment
                ? _glamourer.ApplyDesignFull(designId)
                : _glamourer.ApplyDesignCustomization(designId);

            if (!applied)
                _log.Warning($"[Wardrobe] Base character '{baseChar.Name}': could not apply design " +
                             $"'{baseChar.DesignName}' — it may have been deleted in Glamourer.");
        }

        var worn = 0;
        foreach (var item in ResolveBase(baseChar))
        {
            if (IsItemWorn(item)) continue;
            WearItem(item);
            worn++;
        }

        if (worn > 0)
            _log.Debug($"[Wardrobe] Base character '{baseChar.Name}': re-applied {worn} item(s)");

        return worn;
    }

    /// <summary>
    /// Switches to another base character, taking off the items only the old one wore.
    /// </summary>
    /// <remarks>
    /// Items shared by both are left alone rather than removed and re-applied, and an item whose
    /// slot the new base also fills is displaced by <see cref="WearItem"/> anyway — so the only
    /// thing to undo is what the old base held in a slot the new one has nothing for. Without this,
    /// swapping a Viera base for a Miqo'te one would leave the ears on.
    /// </remarks>
    public void SwitchBase(BaseCharacter? previous, BaseCharacter? next)
    {
        var needsRedraw = false;

        if (previous != null && previous.Id != next?.Id)
        {
            var keep = next?.ItemIds ?? new List<Guid>();
            foreach (var item in ResolveBase(previous))
            {
                if (keep.Contains(item.Id) || !IsItemWorn(item)) continue;
                needsRedraw |= UnwearItem(item, save: false, redraw: false, restoreBase: false);
            }
        }

        _config.ActiveBaseCharacterId = next?.Id;
        _config.Save();

        if (next != null) ApplyBase(next);

        // Only when a removal asked for one, as unwearing a set does: hair and texture mods have
        // nothing else to make them disappear, while gear forces its own reload on the way out
        if (needsRedraw) _penumbra.RedrawPlayer();

        WardrobeChanged?.Invoke();
    }

    /// <summary>
    /// Disables the mods behind everything the character has on, then forces every equipment slot
    /// in Glamourer to its Emperor's New item so the character appears fully unequipped.
    /// </summary>
    /// <remarks>
    /// Emotes, VFX, mounts and minions are left running. Stripping is about what the character is
    /// wearing and none of those are on it, so turning off a dance mod because someone wanted a
    /// bare screenshot would be a surprise — and a quiet one, since nothing about the character
    /// would show it had happened.
    /// <para>
    /// With a base character active this strips down to it rather than down to nothing: its items
    /// stay on, its slots are left as they are, and it is re-applied afterwards so a slot displaced
    /// since the last strip comes back. Nothing changes when there is no base character.
    /// </para>
    /// </remarks>
    /// <param name="ignoreBase">
    /// Take the base character off as well, leaving nothing on at all. The base exists so that a
    /// strip has a floor, and Strip itself always respects it — this is for Unequip All, which is the
    /// wardrobe emptying itself rather than stripping down to something.
    /// </param>
    /// <param name="toNothing">
    /// Set the slots to Glamourer's "Nothing" rather than to the Emperor's New item. Both look bare
    /// on the character, but only one is actually empty: Emperor's New is an invisible item still
    /// sitting in the slot, which is what keeps a mod that redirects it drawing. Strip wants that;
    /// Unequip All, which is meant to leave the character wearing nothing at all, does not.
    /// </param>
    public void StripAll(bool ignoreBase = false, bool toNothing = false)
    {
        // Everything below already does the right thing when there is no base character, so ignoring
        // one is just pretending there isn't one. The only line that cannot be expressed that way is
        // the re-apply at the end, which reaches for the active base itself.
        var baseChar = ignoreBase ? null : _config.ActiveBaseCharacter;
        var kept     = KeptSlots(baseChar);
        var keptIds  = baseChar?.ItemIds ?? new List<Guid>();

        foreach (var (key, id) in _config.WornItems.ToList())
        {
            var item = _config.WardrobeItems.Find(x => x.Id == id);

            // A key whose item has since been deleted can never be cleared by UnwearItem, and the
            // wholesale Clear() that used to follow this loop is gone
            if (item == null)
            {
                _config.WornItems.Remove(key);
                continue;
            }

            if (item.Slot.IsModCategory()) continue;

            // The base character is what a strip strips down to, not part of what it removes
            if (keptIds.Contains(item.Id) || kept.Contains(item.Slot)) continue;

            // Removes its own WornItems entry
            UnwearItem(item, save: false, restoreBase: false);
        }

        // Force every equipment slot empty regardless of what Glamourer currently has.
        // Customisation slots are skipped — stripping cannot remove a character's hair.
        //
        // See toNothing for why there are two kinds of empty and which is which.
        foreach (var slot in EquipSlotEx.All)
        {
            if (slot.IsCustomization() || kept.Contains(slot)) continue;

            // Weapons fall through to Emperor's New either way: their empty is keyed by equip type
            // rather than by slot, so there is no Nothing id to compute for them here
            if (toNothing && _glamourer.SetSlotToNothing(slot)) continue;

            var emperorsId = ItemLookupService.FindEmperorsNewItem(slot);
            if (emperorsId.HasValue)
                _glamourer.SetItem(slot, emperorsId.Value);
        }

        // After the stripping, so a base item that something else had displaced is put back rather
        // than merely spared. Skipped entirely when the base is being stripped too — ApplyBase falls
        // back to the active base when handed null, so passing the null above would put back the very
        // thing this was asked to remove.
        if (!ignoreBase) ApplyBase(baseChar);

        _config.Save();
        WardrobeChanged?.Invoke();
    }

    /// <summary>How an item's stored state compares with what Penumbra and Glamourer actually have.</summary>
    private enum ItemState
    {
        /// <summary>Mods disabled or their options do not match — the item is simply not on.</summary>
        Off,

        /// <summary>Mods enabled with matching options, and Glamourer agrees (or has no say).</summary>
        On,

        /// <summary>
        /// Mods enabled with matching options, but Glamourer has a different item in the slot.
        /// </summary>
        Desynced,
    }

    /// <summary>Result of a scan: what was adopted as worn, and what looks half-applied.</summary>
    /// <param name="Adopted">Item IDs newly recorded in WornItems.</param>
    /// <param name="Desynced">
    /// Items whose Penumbra mods are enabled and correctly configured but which Glamourer is not
    /// showing. Nothing is changed for these — the caller decides what to do about them.
    /// </param>
    public record ScanResult(IReadOnlySet<Guid> Adopted, IReadOnlyList<WardrobeItem> Desynced);

    /// <summary>
    /// Compares one item against Penumbra and Glamourer:
    ///   1. All required mods are enabled in Penumbra
    ///   2. Active mod options match the stored options (if any)
    ///   3. Glamourer has the expected item equipped in the correct slot (if available)
    /// </summary>
    private ItemState Evaluate(WardrobeItem item, Dictionary<string, ulong>? glamEquipment)
    {
        // Check 1: all mods are enabled in Penumbra
        var offMods = item.Mods
            .Where(m => string.IsNullOrEmpty(m.ModDirectory) ||
                        !_penumbra.IsModEnabled(m.Collection, m.ModDirectory, m.ModName))
            .ToList();

        // A mod that is off cannot be one the wardrobe is holding on, so drop any stale claim while
        // the answer is already in hand. Catches the user disabling a mod in Penumbra directly: the
        // claim would otherwise survive, and re-enabling it for a design would leave the wardrobe
        // believing it may switch that mod off again.
        foreach (var mod in offMods)
            if (!string.IsNullOrEmpty(mod.ModDirectory) && _config.ModsEnabledByWardrobe.Remove(ModKey(mod)))
            {
                _prunedClaims = true;
                _log.Debug($"[Wardrobe] Scan: released stale claim on '{mod.ModName}' — it is disabled in Penumbra");
            }

        if (offMods.Count > 0)
        {
            var names = offMods.Select(m => string.IsNullOrEmpty(m.ModDirectory) ? $"{m.ModName} (no dir)" : m.ModName);
            _log.Debug($"[Wardrobe] Scan: '{item.Name}' skipped — mods not enabled in Penumbra: {string.Join(", ", names)}");
            return ItemState.Off;
        }

        // Check 2: mod options must match, single- and multi-select alike.
        // Multi-select was previously excluded because it never matched — that was the
        // TrySetModSetting/TrySetModSettings bug (see PenumbraIpc.ApplyMultiModSettings),
        // not an inherent limitation. Now that the apply path is correct, matching on
        // multi-select again rules out items that share a mod but want different checkboxes.
        var failedMods = item.Mods
            .Where(m => !_penumbra.ActiveOptionsMatch(m.Collection, m.ModDirectory, m.ModName, m.Options,
                m.MultiOptions, m.OptionStates))
            .Select(m => m.ModName)
            .ToList();
        if (failedMods.Count > 0)
        {
            _log.Debug($"[Wardrobe] Scan: '{item.Name}' skipped — options mismatch for: {string.Join(", ", failedMods)}");
            return ItemState.Off;
        }

        // Check 3: if Glamourer state is available and the item has a known item ID,
        // require the Glamourer slot to contain exactly that item.
        // A mismatch is not "not worn": the mod files are live on the character either way. It
        // means the two halves have come apart — most often because Glamourer was reset to game
        // state by a crash or a /glamour disable while Penumbra kept the mod enabled.
        if (item.GlamourerItemId.HasValue && glamEquipment != null)
        {
            var slotName = GlamourerIpc.ToSlotName(item.Slot);
            if (!string.IsNullOrEmpty(slotName) &&
                glamEquipment.TryGetValue(slotName, out var equippedId) &&
                equippedId != item.GlamourerItemId.Value)
            {
                _log.Debug($"[Wardrobe] Scan: '{item.Name}' desynced — Glamourer {slotName} " +
                           $"has {equippedId}, expected {item.GlamourerItemId.Value}");
                return ItemState.Desynced;
            }
        }

        return ItemState.On;
    }

    /// <summary>
    /// Checks which wardrobe items are currently worn and records them in WornItems (first match
    /// wins per key), and reports the ones whose mods are on but whose Glamourer half is missing.
    /// </summary>
    public ScanResult ScanAndSyncWorn() => Scan(adopt: true);

    /// <summary>
    /// Reports items whose mods are enabled but which Glamourer is not showing, changing nothing.
    /// </summary>
    /// <remarks>
    /// Used for the check on opening the window, where silently marking things as worn would be a
    /// state change the user never asked for.
    /// </remarks>
    public IReadOnlyList<WardrobeItem> FindDesynced() => Scan(adopt: false).Desynced;

    /// <summary>Set by <see cref="Evaluate"/> when it drops a stale claim, so the scan can save once.</summary>
    private bool _prunedClaims;

    private ScanResult Scan(bool adopt)
    {
        var added    = new HashSet<Guid>();
        var on       = new List<WardrobeItem>();
        var desynced = new List<WardrobeItem>();

        _prunedClaims = false;

        // Fetch Glamourer's current equipment state once for the whole scan.
        // Keys are Glamourer slot names (Head, Body, RFinger, …); values are item row IDs.
        var glamEquipment = _glamourer.GetAllEquipment();
        _log.Debug($"[Wardrobe] Scan: Glamourer equipment snapshot has {glamEquipment?.Count ?? -1} slots");

        foreach (var item in _config.WardrobeItems)
        {
            if (item.Mods.Count == 0 || item.Slot == EquipSlot.Unknown) continue;

            switch (Evaluate(item, glamEquipment))
            {
                case ItemState.On:
                    on.Add(item);
                    var slotKey = item.WornKey();
                    if (adopt && !_config.WornItems.ContainsKey(slotKey))
                    {
                        _config.WornItems[slotKey] = item.Id;
                        added.Add(item.Id);
                        _log.Debug($"[Wardrobe] Scan: detected '{item.Name}' as worn");
                    }
                    break;

                case ItemState.Desynced:
                    desynced.Add(item);
                    break;
            }
        }

        // An item sharing every one of its mods with something that *is* correctly worn explains
        // itself: those mods are enabled for the other item's sake, not left over from this one.
        // Two items in one slot backed by the same mod — a body and its matching legs, or an item
        // and its variant — would otherwise be reported every single time.
        var explained = on
            .SelectMany(i => i.Mods)
            .Select(m => $"{m.Collection} {m.ModDirectory}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexplained = desynced
            .Where(i => i.Mods.Any(m => !string.IsNullOrEmpty(m.ModDirectory) &&
                                        !explained.Contains($"{m.Collection} {m.ModDirectory}")))
            .ToList();

        if (unexplained.Count > 0)
            _log.Information($"[Wardrobe] Scan: {unexplained.Count} item(s) have mods enabled that " +
                             $"Glamourer is not showing: {string.Join(", ", unexplained.Select(i => i.Name))}");

        if (added.Count > 0 || _prunedClaims) _config.Save();
        return new ScanResult(added, unexplained);
    }

    /// <summary>
    /// Turns off an item's Penumbra mods without touching Glamourer, leaving WornItems alone.
    /// </summary>
    /// <remarks>
    /// For clearing up a desync, where the mods are enabled but Glamourer has whatever the game
    /// gave the character. <see cref="UnwearItem"/> empties the slot to Emperor's New on its way
    /// out, which here would strip real gear the character is legitimately wearing.
    /// Mods another worn item still needs are left enabled, exactly as when unwearing.
    /// </remarks>
    /// <remarks>
    /// The one path that turns off mods the wardrobe never claimed, and the only cleanup route for
    /// leftovers it cannot account for — those an older version enabled before ownership was
    /// tracked, or that a crash stranded. Unwearing must never guess, but this is the user pointing
    /// at named items and asking for exactly this, which is why it may do what unwearing may not.
    /// </remarks>
    public void DisableItemMods(WardrobeItem item)
    {
        var disabledAny = false;

        foreach (var mod in item.Mods)
        {
            if (string.IsNullOrEmpty(mod.ModDirectory)) continue;

            var stillNeeded = _config.WardrobeItems.Any(other =>
                other.Id != item.Id &&
                _config.WornItems.ContainsValue(other.Id) &&
                other.Mods.Any(m =>
                    m.ModDirectory == mod.ModDirectory &&
                    string.Equals(m.Collection, mod.Collection, StringComparison.OrdinalIgnoreCase)));

            if (!stillNeeded && _penumbra.SetModEnabled(mod.Collection, mod.ModDirectory, mod.ModName, false))
            {
                ReleaseMod(mod);
                disabledAny = true;
            }
        }

        _log.Information($"[Wardrobe] Disabled mods for desynced item '{item.Name}'");

        // Nothing swaps a Glamourer item here, so only a redraw makes the change visible
        if (disabledAny) _penumbra.RedrawPlayer();

        // Releasing a claim is a config change, and this path has no caller that saves for it
        if (disabledAny) _config.Save();

        WardrobeChanged?.Invoke();
    }

    // ── Outfits ───────────────────────────────────────────────────────────────

    // Last outfit worn, so redraws can re-apply its dyes. Not persisted: it only describes what is
    // on the character right now, and Glamourer state does not survive a restart either.
    private Guid? _activeOutfitId;

    /// <summary>Dye configured for an item within an outfit, or null when undyed.</summary>
    public static OutfitDye? GetDye(Outfit outfit, Guid itemId) =>
        outfit.Dyes.TryGetValue(itemId.ToString(), out var dye) && !dye.IsUndyed ? dye : null;

    /// <summary>Sets or clears an item's dye within an outfit.</summary>
    /// <remarks>
    /// Advanced dye rows are carried across rather than replaced. They are set from a different
    /// control and mean something different — clearing both channels back to undyed is not a
    /// statement about the colour table, and throwing the rows away here would make a dye picker
    /// quietly destroy work done in Glamourer.
    /// </remarks>
    public void SetDye(Outfit outfit, Guid itemId, byte stain1, byte stain2)
    {
        var key      = itemId.ToString();
        var existing = outfit.Dyes.TryGetValue(key, out var prev) ? prev.Advanced : new();

        if (stain1 == 0 && stain2 == 0 && existing.Count == 0)
            outfit.Dyes.Remove(key);
        else
            outfit.Dyes[key] = new OutfitDye { Stain1 = stain1, Stain2 = stain2, Advanced = existing };

        _config.Save();
    }

    /// <summary>
    /// Stores the advanced dye rows Glamourer currently has for this item's slot.
    /// </summary>
    /// <returns>How many rows were captured. Zero means nothing was advanced dyed on that slot.</returns>
    public int CaptureAdvancedDyes(Outfit outfit, WardrobeItem item)
    {
        var rows = _glamourer.CaptureAdvancedDyes(item.Slot);
        var key  = item.Id.ToString();

        if (rows.Count == 0)
        {
            _log.Debug($"[Wardrobe] No advanced dyes on {item.Slot.DisplayName()} to capture for '{item.Name}'");
            return 0;
        }

        var dye = outfit.Dyes.TryGetValue(key, out var prev) ? prev : new OutfitDye();
        dye.Advanced       = rows;
        outfit.Dyes[key]   = dye;

        _config.Save();
        _log.Debug($"[Wardrobe] Captured {rows.Count} advanced dye row(s) for '{item.Name}' in '{outfit.Name}'");
        return rows.Count;
    }

    /// <summary>
    /// Drops an item's stored advanced dyes, putting the character's own back to game values.
    /// </summary>
    public void ClearAdvancedDyes(Outfit outfit, WardrobeItem item)
    {
        var key = item.Id.ToString();
        if (!outfit.Dyes.TryGetValue(key, out var dye) || dye.Advanced.Count == 0) return;

        // While it is on, forgetting the rows would leave them applied with nothing left to
        // describe them — so the character is put back first, then the record is dropped
        if (IsItemWorn(item))
            _glamourer.RevertAdvancedDyes(dye.Advanced);

        dye.Advanced = new();

        if (dye.IsUndyed)
            outfit.Dyes.Remove(key);

        _config.Save();
    }

    /// <summary>
    /// Sets one dye channel on every dyeable item in an outfit, leaving the other channel alone.
    /// </summary>
    /// <remarks>
    /// A shortcut for the common case of one dye across a whole set. Per-item pickers still win
    /// afterwards, so this is a starting point rather than a lock.
    /// </remarks>
    public void SetDyeAll(Outfit outfit, int channel, byte stain)
    {
        foreach (var item in ResolveOutfit(outfit))
        {
            // Hair, emotes and mounts have no equipment piece to dye
            if (item.Slot.IsModOnly()) continue;

            var dye = GetDye(outfit, item.Id);
            var s1  = channel == 1 ? stain : dye?.Stain1 ?? 0;
            var s2  = channel == 2 ? stain : dye?.Stain2 ?? 0;
            var key = item.Id.ToString();

            // Kept for the same reason SetDye keeps them: dyeing a whole outfit one colour says
            // nothing about anyone's colour tables
            var advanced = dye?.Advanced ?? new();

            // Written here rather than through SetDye so the whole outfit is one save, not one per item
            if (s1 == 0 && s2 == 0 && advanced.Count == 0)
                outfit.Dyes.Remove(key);
            else
                outfit.Dyes[key] = new OutfitDye { Stain1 = s1, Stain2 = s2, Advanced = advanced };
        }

        _config.Save();
    }

    /// <summary>
    /// The dye shared by every dyeable item in an outfit on one channel, or null when they differ.
    /// </summary>
    public byte? CommonDye(Outfit outfit, int channel)
    {
        byte? common = null;

        foreach (var item in ResolveOutfit(outfit))
        {
            if (item.Slot.IsModOnly()) continue;

            var dye  = GetDye(outfit, item.Id);
            var here = channel == 1 ? dye?.Stain1 ?? 0 : dye?.Stain2 ?? 0;

            if (common == null) common = here;
            else if (common != here) return null;
        }

        return common;
    }

    /// <summary>Saves everything currently worn as a named outfit.</summary>
    public Outfit? SaveCurrentAsOutfit(string name)
    {
        var wornIds = _config.WornItems.Values.Distinct().ToList();

        var outfit = new Outfit
        {
            Name    = string.IsNullOrWhiteSpace(name) ? "New Outfit" : name.Trim(),
            ItemIds = wornIds,
        };

        // After ItemIds, since what counts as vanilla is whatever the outfit's own items leave over
        var vanilla = CaptureVanillaItems(outfit);

        // An outfit of nothing but plain gear is a real outfit — the wardrobe having no part in it
        // is not a reason to refuse to remember it
        if (wornIds.Count == 0 && vanilla == 0)
        {
            _log.Warning("[Wardrobe] Nothing is currently worn — no outfit saved.");
            return null;
        }

        _config.Outfits.Add(outfit);
        _config.Save();
        WardrobeChanged?.Invoke();

        _log.Information($"[Wardrobe] Saved outfit '{outfit.Name}' with {wornIds.Count} item(s) " +
                         $"and {outfit.VanillaItems.Count} vanilla piece(s)");
        return outfit;
    }

    /// <summary>
    /// Replaces an outfit's contents with whatever is worn right now, keeping its dyes.
    /// </summary>
    /// <remarks>
    /// For editing an outfit by wearing it and changing pieces on the character, which is the only
    /// way to see what a swap actually looks like. Items still in the outfit keep the dye they had.
    /// A piece swapped into a slot inherits the dye of the piece it replaced, since the dye is
    /// usually chosen for the outfit rather than the item — the new row can be re-dyed afterwards
    /// like any other. Dyes for items no longer worn are dropped: they are no longer part of the
    /// outfit, and keeping them would resurrect an old colour if the item were ever added back.
    /// Returns the number of items the outfit now holds, or null when nothing is worn.
    /// </remarks>
    public int? UpdateOutfitFromWorn(Outfit outfit)
    {
        var wornIds = _config.WornItems.Values.Distinct().ToList();
        if (wornIds.Count == 0 && _glamourer.GetEquippedPieces().Count == 0)
        {
            _log.Warning($"[Wardrobe] Nothing is worn — outfit '{outfit.Name}' left as it was.");
            return null;
        }

        // The outfit's dyes as they stand, by slot, so a replacement can pick up the colour that
        // was on the slot before the swap. Read before ItemIds is replaced.
        var dyeBySlot = new Dictionary<EquipSlot, OutfitDye>();
        foreach (var item in ResolveOutfit(outfit))
            if (GetDye(outfit, item.Id) is { } dye)
                dyeBySlot.TryAdd(item.Slot, dye);

        var dyes = new Dictionary<string, OutfitDye>();
        foreach (var id in wornIds)
        {
            var item = _config.WardrobeItems.Find(x => x.Id == id);
            if (item == null || item.Slot.IsModOnly()) continue;

            // An item that was already in the outfit keeps its own dye; only a newcomer falls back
            // to the slot's
            var dye = GetDye(outfit, id)
                   ?? (dyeBySlot.TryGetValue(item.Slot, out var slotDye) ? slotDye : null);
            if (dye == null) continue;

            // Copied rather than shared: two items pointing at one OutfitDye would re-dye each
            // other when either row is edited
            dyes[id.ToString()] = new OutfitDye { Stain1 = dye.Stain1, Stain2 = dye.Stain2 };
        }

        outfit.ItemIds = wornIds;
        outfit.Dyes    = dyes;

        // After ItemIds, so the slots the new contents cover are the ones excluded from the capture
        CaptureVanillaItems(outfit);

        // What is on the character is now exactly this outfit, so redraws should re-apply its dyes
        _activeOutfitId = outfit.Id;

        _config.Save();
        WardrobeChanged?.Invoke();

        _log.Information($"[Wardrobe] Updated outfit '{outfit.Name}' from what is worn ({wornIds.Count} item(s))");
        return wornIds.Count;
    }

    /// <summary>
    /// Records the plain game items filling the slots this outfit's own items do not.
    /// </summary>
    /// <remarks>
    /// A look is rarely all mods. Without this, saving an outfit kept the two modded pieces and
    /// silently forgot the six vanilla ones, so wearing it back gave a half-dressed character and
    /// the rest of the look had to be rebuilt by hand every time (issue #11).
    /// <para>
    /// Slots the outfit already covers are skipped, and skipped again at wear time: a wardrobe item
    /// carries its mod and options as well as the game item, so it is always the better record and
    /// must never be shadowed by a plain copy of the same slot.
    /// </para>
    /// </remarks>
    public int CaptureVanillaItems(Outfit outfit)
    {
        // Never on a design card. The design supplies the gear for every slot it applies, and vanilla
        // pieces go on after the items — so a capture taken while the design was worn would freeze a
        // copy of the design's own gear and then lay that copy over the live design on every wear. That
        // is precisely the snapshot the link exists to avoid, and it would be invisible: the card would
        // look right and quietly stop following the design. Reached from Update from worn as well as
        // from the button, which is why the guard is here rather than only in the UI.
        if (outfit.IsDesign)
        {
            _log.Debug($"[Wardrobe] '{outfit.Name}' is a design card — skipping the vanilla capture, " +
                       $"since the design already supplies its gear");
            return 0;
        }

        var covered = ResolveOutfit(outfit).Select(i => i.Slot).ToHashSet();
        var live    = _glamourer.GetEquippedPieces();
        var vanilla = new Dictionary<string, VanillaPiece>();

        foreach (var (slot, piece) in live)
        {
            if (covered.Contains(slot)) continue;

            // An Emperor's New piece is how an empty slot is spelled, not something to put back on
            if (ItemLookupService.FindEmperorsNewItem(slot) is { } empty && piece.ItemId == empty) continue;

            vanilla[slot.ToString()] = new VanillaPiece
            {
                ItemId = piece.ItemId,
                Name   = Plugin.ItemLookup.GetItemName(piece.ItemId),
                Stain1 = piece.Stain1,
                Stain2 = piece.Stain2,
            };
        }

        outfit.VanillaItems = vanilla;

        // Information rather than Debug: this is the button people press when an outfit came back
        // wrong, and "what did it actually find" is the first question afterwards
        _log.Information($"[Wardrobe] Outfit '{outfit.Name}': captured {vanilla.Count} vanilla piece(s) " +
                         $"from {live.Count} equipped, {covered.Count} slot(s) covered by its own items");
        return vanilla.Count;
    }

    /// <summary>
    /// Equips an outfit's vanilla pieces, in the slots its own items do not fill.
    /// </summary>
    /// <remarks>
    /// After the items, and only where they left a gap. Checked against the outfit as it stands
    /// rather than as it was saved, so adding a wardrobe item to a slot that used to hold a vanilla
    /// piece leaves the stored piece harmlessly unused rather than fighting the new item for the slot.
    /// </remarks>
    private void WearVanillaItems(Outfit outfit)
    {
        if (outfit.VanillaItems.Count == 0) return;

        var covered = ResolveOutfit(outfit).Select(i => i.Slot).ToHashSet();

        foreach (var (slotName, piece) in outfit.VanillaItems)
        {
            if (!Enum.TryParse<EquipSlot>(slotName, out var slot)) continue;
            if (covered.Contains(slot)) continue;

            _glamourer.SetItem(slot, piece.ItemId, piece.Stain1, piece.Stain2);
        }

        _log.Debug($"[Wardrobe] Outfit '{outfit.Name}': applied {outfit.VanillaItems.Count} vanilla piece(s)");
    }

    /// <summary>Items in an outfit that still exist, in slot order.</summary>
    public List<WardrobeItem> ResolveOutfit(Outfit outfit) =>
        outfit.ItemIds
            .Select(id => _config.WardrobeItems.Find(x => x.Id == id))
            .Where(x => x != null)
            .Select(x => x!)
            .OrderBy(x => (int)x.Slot)
            .ToList();

    /// <summary>
    /// Wears every item in an outfit, optionally removing anything worn that is not part of it.
    /// </summary>
    /// <remarks>
    /// Goes through the normal per-item path, so each item's Penumbra mods are enabled and their
    /// options applied — which is the whole reason for handling outfits here rather than leaving
    /// it to a Glamourer design.
    /// </remarks>
    public void WearOutfit(Outfit outfit, bool removeOthers)
    {
        var items = ResolveOutfit(outfit);
        var missing = outfit.ItemIds.Count - items.Count;
        if (missing > 0)
            _log.Warning($"[Wardrobe] Outfit '{outfit.Name}': {missing} item(s) no longer exist and were skipped.");

        if (removeOthers)
        {
            var keep = items.Select(i => i.Id).ToHashSet();

            // Wearing an outfit exclusively still means wearing it on your own character, so the
            // base character's items and slots are held back here exactly as a strip holds them
            var baseChar = _config.ActiveBaseCharacter;
            var keptSlots = KeptSlots(baseChar);
            foreach (var id in baseChar?.ItemIds ?? new List<Guid>()) keep.Add(id);

            foreach (var wornId in _config.WornItems.Values.ToList())
            {
                if (keep.Contains(wornId)) continue;
                var worn = _config.WardrobeItems.Find(x => x.Id == wornId);

                // Never a slot the outfit itself fills: its own piece is about to take that slot,
                // and holding the old one back would be the base protecting the outfit from itself
                if (worn == null || (keptSlots.Contains(worn.Slot) &&
                                     !items.Any(i => i.Slot == worn.Slot))) continue;

                UnwearItem(worn, save: false, redraw: false, restoreBase: false);
            }
        }

        // Before the items, so the design is the layer they go on over: an item in the outfit wins
        // the slot it occupies, and the design dresses everything the outfit has nothing for. The
        // other way round, the design's gear would replace the very pieces the outfit is made of.
        if (outfit.IsDesign) ApplyOutfitDesign(outfit);

        foreach (var item in items)
            WearItem(item, GetDye(outfit, item.Id));

        // After the items: a vanilla piece only ever fills a gap they left, and equipping it first
        // would put plain gear in a slot a mod is about to take anyway
        WearVanillaItems(outfit);

        // Last, so nothing after it can put a hat back on. A design carries its own hat and weapon
        // state, and applying one is exactly what would override an outfit that asked for the
        // opposite — so the outfit's own answer is written after the design has had its say.
        ApplyOutfitVisibility(outfit);

        // Remembered so redraws re-apply the dyes too, not just the items
        _activeOutfitId = outfit.Id;

        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Wore outfit '{outfit.Name}' ({items.Count} item(s), " +
                         $"{outfit.VanillaItems.Count} vanilla piece(s))");
    }

    /// <summary>Applies an outfit's hat and weapon toggles, where it has an opinion about them.</summary>
    /// <remarks>
    /// Nothing happens for a null, which is the point of it being nullable: an outfit with no opinion
    /// leaves whatever the wearer had set, rather than quietly turning their weapon back on every time
    /// they put a dress on.
    /// <para>
    /// Not undone by <see cref="UnwearOutfit"/>. Taking an outfit off restores the character, not the
    /// session before it, and there is nothing sensible to restore to — the state before the outfit
    /// was worn is long gone by the time several outfits have been tried on. Whoever wants the hat
    /// back toggles it, the same as they would in Glamourer.
    /// </para>
    /// </remarks>
    private void ApplyOutfitVisibility(Outfit outfit)
    {
        if (outfit.HatVisible is { } hat)
        {
            _glamourer.SetHatVisible(hat);
            _log.Debug($"[Wardrobe] Outfit '{outfit.Name}': headgear {(hat ? "shown" : "hidden")}");
        }

        if (outfit.WeaponVisible is { } weapon)
        {
            _glamourer.SetWeaponVisible(weapon);
            _log.Debug($"[Wardrobe] Outfit '{outfit.Name}': weapon {(weapon ? "shown" : "hidden")}");
        }
    }

    /// <summary>Removes every item in an outfit that is currently worn.</summary>
    public void UnwearOutfit(Outfit outfit)
    {
        var items = ResolveOutfit(outfit).Where(IsItemWorn).ToList();

        // One redraw at the end rather than per item — and only if one of them actually needs it.
        // Gear forces its own reload by swapping to Emperor's New, so an outfit of nothing but
        // equipment has no use for a redraw and visibly stutters when given one; hair, animations
        // and texture mods have nothing else to make them disappear and do ask.
        var needsRedraw = false;
        foreach (var item in items)
            needsRedraw |= UnwearItem(item, save: false, redraw: false, restoreBase: false);

        if (_activeOutfitId == outfit.Id) _activeOutfitId = null;

        if (needsRedraw) _penumbra.RedrawPlayer();

        // What taking an outfit off leaves behind is the base character, the same as a strip does
        ApplyBase();

        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Removed outfit '{outfit.Name}' ({items.Count} item(s))");
    }

    /// <summary>True when every item in the outfit is currently worn.</summary>
    public bool IsOutfitWorn(Outfit outfit)
    {
        var items = ResolveOutfit(outfit);
        return items.Count > 0 && items.All(IsItemWorn);
    }

    /// <summary>True when some but not all of the outfit is currently worn.</summary>
    /// <remarks>
    /// Reached by stripping — which deliberately leaves emotes, VFX and mounts running — and by
    /// unequipping a single piece by hand. The outfit is still partly on the character, so it has
    /// to stay removable: judging that on <see cref="IsOutfitWorn"/> alone would hide the control
    /// that turns off the very mods stripping left enabled.
    /// </remarks>
    public bool IsOutfitPartlyWorn(Outfit outfit)
    {
        var items = ResolveOutfit(outfit);
        return items.Any(IsItemWorn) && !items.All(IsItemWorn);
    }

    public void DeleteOutfit(Outfit outfit)
    {
        _config.Outfits.Remove(outfit);
        _config.Save();
        WardrobeChanged?.Invoke();
    }

    // ── Vanilla glamour plates ────────────────────────────────────────────────

    /// <summary>
    /// The style every synced plate is tagged with, so the whole set can be filtered in or out
    /// with the control that already exists rather than a switch of its own.
    /// </summary>
    public static readonly string GlamourPlateStyle = TagTree.StylePath("Glamour Plate");

    /// <summary>Plate outfits, whether or not the game currently has plate data to compare against.</summary>
    public List<Outfit> PlateOutfits() =>
        _config.Outfits.Where(o => o.IsGlamourPlate).OrderBy(o => o.GlamourPlateId).ToList();

    private static Dictionary<string, VanillaPiece> ToVanillaItems(IReadOnlyList<PlateSlotPiece> pieces)
    {
        var items = new Dictionary<string, VanillaPiece>();
        foreach (var piece in pieces)
            items[piece.Slot.ToString()] = new VanillaPiece
            {
                ItemId = piece.ItemId,
                Name   = Plugin.ItemLookup.GetItemName(piece.ItemId),
                Stain1 = piece.Stain1,
                Stain2 = piece.Stain2,
            };
        return items;
    }

    /// <summary>
    /// Brings every readable glamour plate into the outfit list, creating what is missing and
    /// refreshing what has changed. Returns what it did, for the message shown afterwards.
    /// </summary>
    /// <remarks>
    /// Contents are the game's and are overwritten wholesale. Name, preview image and tags are the
    /// user's and are never touched after the outfit is first created — a plate renamed "Ballroom"
    /// with a photograph taken of it stays that way through every resync, or the feature would
    /// punish anyone who used it.
    /// </remarks>
    public (int Created, int Updated) SyncGlamourPlates()
    {
        Plugin.GlamourPlates.Invalidate();
        var live = Plugin.GlamourPlates.ReadPlates();
        if (live.Count == 0) return (0, 0);

        var created = 0;
        var updated = 0;

        foreach (var plate in live)
        {
            var outfit = _config.Outfits.Find(o => o.GlamourPlateId == plate.PlateId);

            if (outfit == null)
            {
                outfit = new Outfit
                {
                    Name           = $"Glamour Plate {plate.PlateId}",
                    GlamourPlateId = plate.PlateId,
                    Tags           = new List<string> { GlamourPlateStyle },
                };
                _config.Outfits.Add(outfit);
                created++;
            }
            else if (GlamourPlateService.Signature(outfit.VanillaItems)
                  == GlamourPlateService.Signature(plate.Pieces))
            {
                continue;
            }
            else
            {
                updated++;
            }

            ApplyPlate(outfit, plate);
        }

        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Glamour plates synced: {created} created, {updated} updated, " +
                         $"{live.Count} readable");
        return (created, updated);
    }

    /// <summary>Refreshes one plate outfit from the game. False when its plate cannot be read.</summary>
    public bool SyncGlamourPlate(Outfit outfit)
    {
        if (outfit.GlamourPlateId is not { } id) return false;

        Plugin.GlamourPlates.Invalidate();
        if (Plugin.GlamourPlates.ReadPlate(id) is not { } plate) return false;

        ApplyPlate(outfit, plate);
        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Resynced '{outfit.Name}' from glamour plate {id} " +
                         $"({plate.Pieces.Count} piece(s))");
        return true;
    }

    private static void ApplyPlate(Outfit outfit, LivePlate plate)
    {
        outfit.VanillaItems  = ToVanillaItems(plate.Pieces);
        outfit.PlateSyncedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Plate outfits whose stored copy no longer matches the plate in the game.
    /// </summary>
    /// <remarks>
    /// Empty whenever the client has no plate data, so a session that has not been near a
    /// summoning bell reads as "nothing to say" rather than as drift. Silence is the honest answer
    /// there: nothing has been compared.
    /// </remarks>
    public List<Outfit> DesyncedPlates()
    {
        if (!Plugin.GlamourPlates.PlatesLoaded) return new List<Outfit>();

        var live = Plugin.GlamourPlates.ReadPlates().ToDictionary(p => p.PlateId);
        return _config.Outfits
            .Where(o => o.IsGlamourPlate
                     && live.TryGetValue(o.GlamourPlateId!.Value, out var plate)
                     && GlamourPlateService.Signature(plate.Pieces)
                     != GlamourPlateService.Signature(o.VanillaItems))
            .OrderBy(o => o.GlamourPlateId)
            .ToList();
    }

    /// <summary>
    /// Plate outfits whose plate has been emptied in the game.
    /// </summary>
    /// <remarks>
    /// Reported rather than deleted. The stored copy is still a wearable look and may well be the
    /// only record left of it, so throwing it away on the strength of an in-game edit would destroy
    /// exactly what someone cleared the plate to make room for.
    /// </remarks>
    public List<Outfit> OrphanedPlates()
    {
        if (!Plugin.GlamourPlates.PlatesLoaded) return new List<Outfit>();

        var live = Plugin.GlamourPlates.ReadPlates().Select(p => p.PlateId).ToHashSet();
        return _config.Outfits
            .Where(o => o.IsGlamourPlate && !live.Contains(o.GlamourPlateId!.Value))
            .OrderBy(o => o.GlamourPlateId)
            .ToList();
    }

    /// <summary>Plates in the game that have not been brought into the wardrobe yet.</summary>
    public int UnsyncedPlateCount()
    {
        if (!Plugin.GlamourPlates.PlatesLoaded) return 0;

        var known = _config.Outfits.Where(o => o.IsGlamourPlate)
                                   .Select(o => o.GlamourPlateId!.Value).ToHashSet();
        return Plugin.GlamourPlates.ReadPlates().Count(p => !known.Contains(p.PlateId));
    }

    // ── Glamourer designs ─────────────────────────────────────────────────────

    /// <summary>
    /// The style every design card is tagged with, so the whole set can be filtered in or out with the
    /// control that already exists — the same trick <see cref="GlamourPlateStyle"/> plays.
    /// </summary>
    /// <remarks>
    /// Given on creation and then the user's to remove: it is an ordinary style, and a card that has
    /// been filed under styles of its own has no need of it. Removing it is also what tells
    /// <see cref="HoldsUserContent"/> that a card has been looked after.
    /// </remarks>
    public static readonly string DesignStyle = TagTree.StylePath("Glamourer Design");

    /// <summary>Design cards, whether or not Glamourer is currently answering.</summary>
    public List<Outfit> DesignOutfits() =>
        _config.Outfits.Where(o => o.IsDesign)
                       .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList();

    /// <summary>
    /// Brings the design cards in line with Glamourer's design list, which is their only authority.
    /// </summary>
    /// <remarks>
    /// A design is not copied into the wardrobe and then kept up to date — it is linked. Glamourer owns
    /// which designs exist and what they are called, so this runs off the design list every time the
    /// outfits grid draws: a design saved there gains a card without anyone pressing sync, a rename
    /// follows through to the card, and a deletion takes the card with it.
    /// <para>
    /// What the wardrobe keeps is the card's own side — pictures, tags, dyes, and the items whose mods
    /// should go on with the design. So a card holding any of that is <b>never</b> deleted when its
    /// design disappears: it is left behind for <see cref="StrandedDesignCards"/> to report, because
    /// that content is the user's work and not Glamourer's to take away. A card holding nothing but the
    /// link is deleted, since there is nothing in it to lose and a wardrobe should not accumulate a
    /// record for every design ever browsed.
    /// </para>
    /// <para>
    /// Called from the draw loop, so it saves only when something actually changed — and reads the
    /// design list through the IPC's half-second cache rather than asking Glamourer every frame.
    /// </para>
    /// </remarks>
    /// <returns>True when the card list changed, so the caller can redraw or report.</returns>
    public bool ReconcileDesignCards()
    {
        if (!_config.ShowGlamourerDesigns) return false;

        var live = _glamourer.GetDesignsCached();

        // Nothing to reconcile against. An empty list means Glamourer is not answering or holds no
        // designs, and treating that as "every design was deleted" would clear every card on a frame
        // where the answer is simply not in yet.
        if (live.Count == 0) return false;

        var changed = false;
        var known   = new Dictionary<Guid, Outfit>();

        foreach (var card in _config.Outfits.Where(o => o.IsDesign))
            known.TryAdd(card.DesignId!.Value, card);

        foreach (var (id, name) in live)
        {
            if (known.TryGetValue(id, out var card))
            {
                // The name is Glamourer's, so it follows without asking. Nothing else here is.
                if (string.Equals(card.Name, name, StringComparison.Ordinal) &&
                    string.Equals(card.DesignName, name, StringComparison.Ordinal))
                    continue;

                _log.Debug($"[Wardrobe] Design card '{card.Name}' follows its design's new name '{name}'");
                card.Name       = name;
                card.DesignName = name;
                changed         = true;
                continue;
            }

            _config.Outfits.Add(new Outfit
            {
                Name       = name,
                DesignId   = id,
                DesignName = name,
                Tags       = new List<string> { DesignStyle },
            });
            changed = true;
        }

        // Designs that have gone from Glamourer. Only the empty cards go with them.
        var ids = live.Select(d => d.Id).ToHashSet();
        foreach (var (id, card) in known)
        {
            if (ids.Contains(id) || HoldsUserContent(card)) continue;

            _log.Debug($"[Wardrobe] Dropping empty design card '{card.Name}' — its design is gone " +
                       $"from Glamourer and there was nothing attached to it");
            _config.Outfits.Remove(card);
            changed = true;
        }

        if (!changed) return false;

        _config.Save();
        WardrobeChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Whether anything on this card came from the user rather than from the link.
    /// </summary>
    /// <remarks>
    /// What decides between forgetting a card and keeping it when its design disappears. The design
    /// style tag is not counted: every card is given it on creation, so counting it would make every
    /// card look hand-edited and nothing would ever be tidied away.
    /// </remarks>
    private static bool HoldsUserContent(Outfit card) =>
        card.ItemIds.Count > 0
        || card.Dyes.Count > 0
        || card.VanillaItems.Count > 0
        || card.ImageCount() > 0
        || card.Tags.Any(t => !string.Equals(t, DesignStyle, StringComparison.Ordinal))
        || !card.DesignAppliesEquipment;

    /// <summary>
    /// Design cards whose design no longer exists in Glamourer, but which hold something worth keeping.
    /// </summary>
    /// <remarks>
    /// Reported rather than deleted, and never while Glamourer is answering with nothing at all — an
    /// empty design list means the plugin is not there or has none, which is not the same as every
    /// design having been deleted. The same silence <see cref="DesyncedPlates"/> keeps when the client
    /// holds no plate data.
    /// </remarks>
    public List<Outfit> StrandedDesignCards()
    {
        var live = _glamourer.GetDesignsCached();
        if (live.Count == 0) return new List<Outfit>();

        var ids = live.Select(d => d.Id).ToHashSet();
        return DesignOutfits().Where(o => !ids.Contains(o.DesignId!.Value)).ToList();
    }

    /// <summary>
    /// Deletes the stranded cards, for someone who has decided their designs are gone for good.
    /// </summary>
    /// <remarks>
    /// Only ever from a button. The items attached to them are kept — this deletes the cards, exactly
    /// as deleting an outfit does, and the wardrobe items themselves are untouched.
    /// </remarks>
    public int ForgetStrandedDesignCards()
    {
        var stranded = StrandedDesignCards();
        if (stranded.Count == 0) return 0;

        foreach (var card in stranded)
        {
            _log.Information($"[Wardrobe] Forgetting design card '{card.Name}' — its design is gone " +
                             $"from Glamourer");
            _config.Outfits.Remove(card);
        }

        _config.Save();
        WardrobeChanged?.Invoke();
        return stranded.Count;
    }

    /// <summary>
    /// What a design card's design contains, or null while Glamourer has not been asked yet.
    /// </summary>
    /// <remarks>
    /// Read live rather than stored on the card. A design's gear is Glamourer's, and copying it here
    /// would be the sync model again — a list that quietly went stale the moment the design was edited.
    /// The read is cached and rationed inside <see cref="GlamourerIpc"/>, so asking once per card per
    /// frame is what it is built for.
    /// </remarks>
    public GlamourerIpc.DesignContents? DesignContents(Outfit card) =>
        card.DesignId is { } id ? _glamourer.GetDesignContents(id) : null;

    /// <summary>
    /// The dyes the design puts on a slot, or null when it fills that slot with nothing dyed.
    /// </summary>
    /// <remarks>
    /// What a mod attached to a design card should be dyed. The card exists to make sure the mods behind
    /// a look are enabled, so the colour that look was built in belongs to the design rather than being
    /// chosen again by hand — and getting it wrong is not obvious on screen, because the piece is right
    /// and only the colour is off.
    /// <para>
    /// Null for an undyed piece as well as for a slot the design leaves alone: storing two zeroes would
    /// write an <see cref="OutfitDye"/> that says nothing, which the rest of the dye code treats as
    /// absent anyway.
    /// </para>
    /// </remarks>
    public OutfitDye? DesignDyeFor(Outfit card, EquipSlot slot)
    {
        if (DesignContents(card) is not { } contents) return null;

        foreach (var piece in contents.Pieces)
        {
            if (piece.Slot != slot) continue;
            if (piece.Stain1 == 0 && piece.Stain2 == 0) return null;

            return new OutfitDye { Stain1 = piece.Stain1, Stain2 = piece.Stain2 };
        }

        return null;
    }

    /// <summary>
    /// Gives each of a design card's items the dyes the design uses in its slot.
    /// </summary>
    /// <remarks>
    /// Explicit rather than continuous. The design's colours can change in Glamourer at any time, and
    /// re-reading them onto the items every frame would quietly overwrite a colour the user had chosen
    /// for a mod on purpose — which is the one thing they cannot undo. So it is a button, and pressing it
    /// again after editing the design is how the two are brought back together.
    /// </remarks>
    /// <returns>How many items were given a dye.</returns>
    public int InheritDesignDyes(Outfit card)
    {
        if (!card.IsDesign) return 0;

        var applied = 0;

        foreach (var item in ResolveOutfit(card))
        {
            if (DesignDyeFor(card, item.Slot) is not { } dye) continue;

            var key = item.Id.ToString();

            // The advanced rows are the user's and say nothing about the two dye channels, so they are
            // kept exactly as SetDyeAll keeps them
            var advanced = GetDye(card, item.Id)?.Advanced ?? new Dictionary<string, string>();

            card.Dyes[key] = new OutfitDye
            {
                Stain1   = dye.Stain1,
                Stain2   = dye.Stain2,
                Advanced = advanced,
            };
            applied++;
        }

        if (applied == 0) return 0;

        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] '{card.Name}': {applied} item(s) took their dyes from the design");
        return applied;
    }

    /// <summary>
    /// How many of a design card's items the design has a dye for, for a control to say so before it is
    /// pressed.
    /// </summary>
    public int DesignDyeableCount(Outfit card) =>
        card.IsDesign ? ResolveOutfit(card).Count(i => DesignDyeFor(card, i.Slot) != null) : 0;

    /// <summary>True when this card's design is still in Glamourer's list.</summary>
    /// <remarks>
    /// Answers "no" only when Glamourer has answered with a list that does not contain it — a silent
    /// Glamourer means unknown, which is reported as still linked rather than as gone.
    /// </remarks>
    public bool DesignIsLive(Outfit card)
    {
        if (card.DesignId is not { } id) return false;

        var live = _glamourer.GetDesignsCached();
        if (live.Count == 0) return true;

        foreach (var (designId, _) in live)
            if (designId == id) return true;

        return false;
    }

    /// <summary>
    /// Applies a design outfit's design, as much of it as the outfit asks for.
    /// </summary>
    /// <remarks>
    /// Called on the way into <see cref="WearOutfit"/>, before the items, so the design is the layer
    /// underneath and any item in the outfit wins the slot it occupies. Also on its own from the
    /// editor, for putting a design back after something else has changed the character.
    /// </remarks>
    public bool ApplyOutfitDesign(Outfit outfit)
    {
        if (outfit.DesignId is not { } id) return false;

        var ok = outfit.DesignAppliesEquipment
            ? _glamourer.ApplyDesignFull(id)
            : _glamourer.ApplyDesignCustomization(id);

        if (!ok)
            _log.Warning($"[Wardrobe] '{outfit.Name}': could not apply Glamourer design " +
                         $"'{outfit.DesignName}' — it may have been deleted in Glamourer.");

        return ok;
    }

    /// <summary>
    /// Drops every override the wardrobe is holding, so the character shows what the game has on.
    /// </summary>
    /// <remarks>
    /// Glamourer sits on top of the game, so applying a plate for real changes nothing you can see
    /// while the wardrobe still has clothes on the character — the plate lands underneath an
    /// override that hides it. This is what makes the result visible.
    /// <para>
    /// The items are unworn properly rather than merely reverted in Glamourer. A bare revert would
    /// leave their Penumbra mods enabled with nothing showing them, which is exactly the state the
    /// desync notice exists to complain about; there is no sense in building a button that
    /// manufactures it.
    /// </para>
    /// <para>
    /// The base character then goes back on, unless
    /// <see cref="Configuration.KeepBaseCharacterOnRevert"/> says otherwise. Its items are taken off
    /// with the rest first: the revert wipes their Glamourer state whatever the wardrobe believes,
    /// so sparing them here would leave the wardrobe holding items the character is not wearing.
    /// Anything worn in one of its kept slots is put back the same way, so a plate applied for real
    /// lands underneath the character rather than over the top of it.
    /// </para>
    /// </remarks>
    /// <returns>How many items were taken off.</returns>
    public int RevertToInGameLook()
    {
        var baseChar = _config.KeepBaseCharacterOnRevert ? _config.ActiveBaseCharacter : null;
        var kept     = KeptSlots(baseChar);

        // Items worn in a slot the base holds but which the base does not name itself — the tail on a
        // ring that was never added to its item list. They come off with everything else, because
        // RevertState wipes their Glamourer half whatever the wardrobe believes, and go back on
        // afterwards. Without this, "kept" would mean one thing for a strip and another for a plate.
        var keptItems = new List<WardrobeItem>();
        var removed   = 0;

        foreach (var (key, id) in _config.WornItems.ToList())
        {
            var item = _config.WardrobeItems.Find(x => x.Id == id);
            if (item == null)
            {
                _config.WornItems.Remove(key);
                continue;
            }

            // Animations, VFX and mounts are not on the character and a glamour plate has nothing to
            // say about them, so a dance left running keeps running — the same line StripAll draws
            if (item.Slot.IsModCategory()) continue;

            if (kept.Contains(item.Slot) && baseChar?.ItemIds.Contains(item.Id) != true)
                keptItems.Add(item);

            UnwearItem(item, save: false, redraw: false, restoreBase: false);
            removed++;
        }

        // After the unwearing, so the Emperor's New pieces it puts in the slots on the way out go
        // too. Without this the character would be standing there stripped rather than in the gear
        // the game has on them.
        _glamourer.RevertState();

        // The kept slots first and the base's own items second, so a base that names an item for a
        // slot wins over whatever merely happened to be worn there
        foreach (var item in keptItems) WearItem(item);
        if (baseChar != null) ApplyBase(baseChar);

        // Customisation mods have nothing but a redraw to make them appear or disappear, and both
        // taking them off and putting the base back on are changes of exactly that kind. Skipped
        // when neither happened, so pressing this twice does not stutter the character for nothing.
        if (removed > 0 || baseChar != null) _penumbra.RedrawPlayer();

        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Reverted to the in-game look ({removed} item(s) taken off, " +
                         $"base character {(baseChar != null ? $"'{baseChar.Name}' re-applied" : "not re-applied")}" +
                         $"{(keptItems.Count > 0 ? $", {keptItems.Count} item(s) put back in kept slots" : string.Empty)})");
        return removed;
    }

    /// <summary>
    /// Applies a plate for real and then shows the result.
    /// </summary>
    /// <remarks>
    /// The apply is a server round trip and is not finished when the call returns, so the revert
    /// waits for the game to say it has stopped applying rather than firing straight away. Reverting
    /// too early is harmless in itself, but re-applying the base character on top of a plate that is
    /// still landing is a race worth not running.
    /// </remarks>
    public bool ApplyPlateInGame(Outfit outfit, out string message)
    {
        if (outfit.GlamourPlateId is not { } plateId)
        {
            message = "This outfit is not a glamour plate.";
            return false;
        }

        if (!Plugin.GlamourPlates.ApplyInGame(plateId, out message)) return false;

        _ = Task.Run(async () =>
        {
            // Polled rather than waited on a fixed delay: the round trip is as quick as the server
            // is. The cap stops a lost reply leaving the wardrobe's clothes on forever.
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                if (!await _framework.RunOnFrameworkThread(() => Plugin.GlamourPlates.IsApplying))
                    break;
            }

            await _framework.RunOnFrameworkThread(() => RevertToInGameLook());
        });

        return true;
    }

    /// <summary>
    /// Copies a plate or a design into an ordinary outfit that owes nothing to its source.
    /// </summary>
    /// <remarks>
    /// The way out of a card the wardrobe does not own. A plate is a fine starting point for a modded
    /// look, and this gives somewhere to build it that resyncing will never overwrite. For a design
    /// outfit the tie itself is what is being cut: the copy keeps the items and dyes and stops
    /// applying the design, which is what someone wants when a look has outgrown the design behind it.
    /// The preview image is deliberately left behind — the copy is about to stop looking like it.
    /// </remarks>
    public Outfit DuplicateAsOutfit(Outfit source)
    {
        // No preview image: the copy exists to be changed, and it is about to stop looking like the
        // photograph taken of the original
        var copy = CopyOutfit(source, keepImage: false);
        copy.Tags.Remove(GlamourPlateStyle);
        copy.Tags.Remove(DesignStyle);

        _config.Outfits.Add(copy);
        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Copied '{source.Name}' to editable outfit '{copy.Name}'");
        return copy;
    }

    /// <summary>
    /// Copies an outfit, contents and all, as a new outfit of its own.
    /// </summary>
    /// <remarks>
    /// The starting point for a variant: the same look with one piece swapped, or the same set of
    /// items dyed a different colour. Rebuilding that from scratch is the work the outfit was saved
    /// to avoid.
    /// <para>
    /// For wardrobe outfits only. A copy never carries a plate's or a design's identity: two outfits
    /// claiming the same plate number, or the same design, would leave the sync updating whichever it
    /// found first and quietly ignoring the other. <see cref="DuplicateAsOutfit"/> is what copies
    /// either of those, and it cuts the tie to the source rather than cloning it.
    /// </para>
    /// </remarks>
    public Outfit DuplicateOutfit(Outfit source)
    {
        var copy = CopyOutfit(source, keepImage: true);
        _config.Outfits.Add(copy);
        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Duplicated outfit '{source.Name}' as '{copy.Name}'");
        return copy;
    }

    /// <summary>
    /// A deep copy of an outfit's contents under a name not already taken.
    /// </summary>
    /// <remarks>
    /// Every collection is rebuilt rather than shared. Two outfits pointing at one
    /// <see cref="OutfitDye"/> would re-dye each other, which is the same trap
    /// <see cref="UpdateOutfitFromWorn"/> avoids when it rebuilds its dye map.
    /// <para>
    /// Not added to the config here — the callers differ in what they strip off afterwards, and an
    /// outfit that appeared in the list before it was finished would be a copy of the wrong thing.
    /// </para>
    /// </remarks>
    private Outfit CopyOutfit(Outfit source, bool keepImage) => new()
    {
        Name         = UniqueOutfitName(source.Name),
        ImagePath    = keepImage ? source.ImagePath : null,
        // The other angles go with the cover or not at all: a copy holding four pictures of a look it
        // no longer has would be worse than a copy holding none
        ExtraImages  = keepImage ? new List<string>(source.ExtraImages) : new List<string>(),
        ItemIds      = new List<Guid>(source.ItemIds),
        Tags         = new List<string>(source.Tags),
        Dyes         = source.Dyes.ToDictionary(
            kv => kv.Key,
            kv => new OutfitDye
            {
                Stain1   = kv.Value.Stain1,
                Stain2   = kv.Value.Stain2,
                Advanced = new Dictionary<string, string>(kv.Value.Advanced),
            }),
        VanillaItems = source.VanillaItems.ToDictionary(
            kv => kv.Key,
            kv => new VanillaPiece
            {
                ItemId = kv.Value.ItemId,
                Name   = kv.Value.Name,
                Stain1 = kv.Value.Stain1,
                Stain2 = kv.Value.Stain2,
            }),

        // Carried, unlike the design and plate links above: those are what a copy is deliberately cut
        // loose from, while a hood being off is part of the look the copy is starting from
        HatVisible    = source.HatVisible,
        WeaponVisible = source.WeaponVisible,
    };

    /// <summary>
    /// "Beach Day" becomes "Beach Day (copy)", then "Beach Day (copy 2)", and so on.
    /// </summary>
    /// <remarks>
    /// Duplicating twice is a normal thing to do — three variants of one look start as three copies
    /// — and three cards all reading "Beach Day (copy)" would leave the grid unusable at exactly the
    /// moment the feature was working.
    /// </remarks>
    private string UniqueOutfitName(string baseName)
    {
        bool Taken(string name) =>
            _config.Outfits.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        var candidate = $"{baseName} (copy)";
        for (var n = 2; Taken(candidate); n++)
            candidate = $"{baseName} (copy {n})";

        return candidate;
    }
}
