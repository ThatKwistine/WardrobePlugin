using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    public bool UnwearItem(WardrobeItem item, bool save = true, bool redraw = true)
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

        var needsRedraw = disabledAny && !swappedItem;
        if (redraw && needsRedraw)
        {
            _log.Debug($"[Wardrobe] UnwearItem: redrawing for '{item.Name}' — no Glamourer item swap to force a reload");
            _penumbra.RedrawPlayer();
            needsRedraw = false;
        }

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
        var needsRedraw = UnwearItem(item, save: false, redraw: false);

        foreach (var partner in worn)
        {
            _log.Debug($"[Wardrobe] '{item.Name}' also removes linked item '{partner.Name}'");
            needsRedraw |= UnwearItem(partner, save: false, redraw: false);
        }

        if (needsRedraw) _penumbra.RedrawPlayer();

        _log.Information($"[Wardrobe] Removed '{item.Name}' with {worn.Count} linked item(s)");

        _config.Save();
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
    /// </remarks>
    public void StripAll()
    {
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

            // Removes its own WornItems entry
            UnwearItem(item, save: false);
        }

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
            .Select(m => $"{m.Collection} {m.ModDirectory}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexplained = desynced
            .Where(i => i.Mods.Any(m => !string.IsNullOrEmpty(m.ModDirectory) &&
                                        !explained.Contains($"{m.Collection} {m.ModDirectory}")))
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
            foreach (var wornId in _config.WornItems.Values.ToList())
            {
                if (keep.Contains(wornId)) continue;
                var worn = _config.WardrobeItems.Find(x => x.Id == wornId);
                if (worn != null) UnwearItem(worn, save: false, redraw: false);
            }
        }

        foreach (var item in items)
            WearItem(item, GetDye(outfit, item.Id));

        // After the items: a vanilla piece only ever fills a gap they left, and equipping it first
        // would put plain gear in a slot a mod is about to take anyway
        WearVanillaItems(outfit);

        // Remembered so redraws re-apply the dyes too, not just the items
        _activeOutfitId = outfit.Id;

        _config.Save();
        WardrobeChanged?.Invoke();
        _log.Information($"[Wardrobe] Wore outfit '{outfit.Name}' ({items.Count} item(s), " +
                         $"{outfit.VanillaItems.Count} vanilla piece(s))");
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
            needsRedraw |= UnwearItem(item, save: false, redraw: false);

        if (_activeOutfitId == outfit.Id) _activeOutfitId = null;

        if (needsRedraw) _penumbra.RedrawPlayer();

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
}
