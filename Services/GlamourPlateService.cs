using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>One piece of a vanilla glamour plate, in the wardrobe's own slot vocabulary.</summary>
public sealed record PlateSlotPiece(EquipSlot Slot, ulong ItemId, byte Stain1, byte Stain2);

/// <summary>A glamour plate as the game currently holds it.</summary>
public sealed record LivePlate(int PlateId, IReadOnlyList<PlateSlotPiece> Pieces);

/// <summary>
/// Reads the character's vanilla glamour plates out of the game client.
/// </summary>
/// <remarks>
/// The only place in the plugin that touches <c>MirageManager</c>. Everything downstream works in
/// <see cref="PlateSlotPiece"/>, which is the same shape as an outfit's vanilla pieces — an item ID
/// and two dye channels per slot — so a plate can be stored and worn through the paths that already
/// exist rather than a second system beside them.
/// <para>
/// Plates only exist in memory once the server has sent them, which it does when the player opens
/// the Glamour Plate window at a summoning bell, an inn or the Glamour Dresser. Until then
/// <see cref="PlatesLoaded"/> is false and there is nothing to read. This is a limit of where the
/// data lives, not of this plugin. Wearing a plate that has already been read has no such limit:
/// it goes through Glamourer like any other outfit, so it works anywhere, gpose included.
/// </para>
/// </remarks>
public sealed unsafe class GlamourPlateService
{
    /// <summary>Plates the game keeps, and pieces in each. Both are fixed by the client.</summary>
    public const int PlateCount = 20;
    private const int SlotCount = 12;

    /// <summary>
    /// Marks an item ID as high quality. Plates keep the flag even though glamour ignores quality,
    /// so it has to come off before the ID means anything to Glamourer or to the item sheet.
    /// </summary>
    private const ulong HqOffset = 1_000_000;

    /// <summary>
    /// Which slot each of a plate's twelve entries is, in the order the client stores them.
    /// </summary>
    /// <remarks>
    /// The client gives no names for these — a plate is twelve parallel arrays and the meaning is
    /// entirely positional. Getting it wrong is quiet rather than loud: rings swap hands, or every
    /// piece lands one slot out. The first sync of a session logs the mapping it used against the
    /// item names it resolved, so the guess can be checked against the plate on screen instead of
    /// being taken on trust.
    /// </remarks>
    private static readonly EquipSlot[] SlotOrder =
    {
        EquipSlot.MainHand, EquipSlot.OffHand,
        EquipSlot.Head, EquipSlot.Body, EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet,
        EquipSlot.Ears, EquipSlot.Neck, EquipSlot.Wrists,
        EquipSlot.RingRight, EquipSlot.RingLeft,
    };

    private readonly IPluginLog _log;

    /// <summary>
    /// Reading twenty plates is cheap, but it happens from the outfits draw loop, so the result is
    /// held briefly rather than recomputed every frame.
    /// </summary>
    private const int CacheMs = 500;

    private IReadOnlyList<LivePlate> _cache = Array.Empty<LivePlate>();
    private DateTime                 _cachedAt = DateTime.MinValue;
    private bool                     _loggedMapping;

    public GlamourPlateService(IPluginLog log) => _log = log;

    /// <summary>True when the client is holding plate data that can be read.</summary>
    public bool PlatesLoaded
    {
        get
        {
            var mgr = MirageManager.Instance();
            return mgr != null && mgr->GlamourPlatesLoaded;
        }
    }

    /// <summary>
    /// Every plate that has something in it, newest read or a cached one no more than
    /// <see cref="CacheMs"/> old. Empty when the client has no plate data.
    /// </summary>
    public IReadOnlyList<LivePlate> ReadPlates()
    {
        if ((DateTime.UtcNow - _cachedAt).TotalMilliseconds < CacheMs) return _cache;

        _cache    = ReadPlatesUncached();
        _cachedAt = DateTime.UtcNow;
        return _cache;
    }

    /// <summary>The live plate with this ID, or null when it is empty or unreadable.</summary>
    public LivePlate? ReadPlate(int plateId) =>
        ReadPlates().FirstOrDefault(p => p.PlateId == plateId);

    /// <summary>Drops the cache, so the next read goes to the game.</summary>
    public void Invalidate() => _cachedAt = DateTime.MinValue;

    private IReadOnlyList<LivePlate> ReadPlatesUncached()
    {
        var mgr = MirageManager.Instance();
        if (mgr == null || !mgr->GlamourPlatesLoaded) return Array.Empty<LivePlate>();

        var plates = new List<LivePlate>();

        for (var i = 0; i < PlateCount; i++)
        {
            var plate  = mgr->GlamourPlates[i];
            var pieces = new List<PlateSlotPiece>();

            for (var s = 0; s < SlotCount; s++)
            {
                var raw = plate.ItemIds[s];

                // Zero is how an empty slot is spelled. Skipped rather than recorded as a blank, so
                // a plate with no hat says nothing about the head rather than saying "nothing" —
                // the same choice CaptureVanillaItems makes for an Emperor's New piece.
                if (raw == 0) continue;

                var itemId = raw >= HqOffset ? raw - HqOffset : raw;
                pieces.Add(new PlateSlotPiece(SlotOrder[s], itemId, plate.Stain0Ids[s], plate.Stain1Ids[s]));
            }

            if (pieces.Count > 0) plates.Add(new LivePlate(i + 1, pieces));
        }

        LogMappingOnce(plates);
        return plates;
    }

    /// <summary>
    /// Writes the slot mapping out once a session, resolved to item names.
    /// </summary>
    /// <remarks>
    /// The positional guess in <see cref="SlotOrder"/> cannot be verified from inside the plugin —
    /// only against the plate the player can see. This is what makes that comparison possible
    /// without a debugger.
    /// </remarks>
    private void LogMappingOnce(IReadOnlyList<LivePlate> plates)
    {
        if (_loggedMapping || plates.Count == 0) return;
        _loggedMapping = true;

        var first = plates[0];
        _log.Information($"[Wardrobe] Glamour plate {first.PlateId} read as:");
        foreach (var piece in first.Pieces)
            _log.Information($"[Wardrobe]   {piece.Slot.DisplayName()} = " +
                             $"{Plugin.ItemLookup.GetItemName(piece.ItemId)} " +
                             $"(id {piece.ItemId}, dyes {piece.Stain1}/{piece.Stain2})");
    }

    // ── Applying a plate for real ─────────────────────────────────────────────

    /// <summary>True while the game is in the middle of applying a plate.</summary>
    /// <remarks>
    /// The apply is a server round trip, so it is not finished when <see cref="ApplyInGame"/>
    /// returns. Anything that wants to see the result has to wait for this to clear.
    /// </remarks>
    public bool IsApplying
    {
        get
        {
            var mgr = MirageManager.Instance();
            return mgr != null && mgr->IsApplyingGlamourPlate;
        }
    }

    /// <summary>
    /// Whether the game would let a glamour plate be applied right now, and why not if it would not.
    /// </summary>
    /// <remarks>
    /// <see cref="UIGlobals.CanApplyGlamourPlates"/> is the game's own gate — the same one that greys
    /// the option out in the gear set list — so this asks it rather than trying to reimplement the
    /// rules about duties, combat and where you are standing.
    /// </remarks>
    public bool CanApplyInGame(out string reason)
    {
        var mirage = MirageManager.Instance();
        if (mirage != null && mirage->IsApplyingGlamourPlate)
        {
            reason = "A glamour plate is already being applied.";
            return false;
        }

        if (!UIGlobals.CanApplyGlamourPlates(true))
        {
            reason = "The game will not apply glamour plates right now — you cannot be in combat, " +
                     "in a duty, or somewhere plates are not allowed.";
            return false;
        }

        var gearsets = RaptureGearsetModule.Instance();
        if (gearsets == null)
        {
            reason = "Gear sets are not available yet.";
            return false;
        }

        if (!gearsets->IsValidGearset(gearsets->CurrentGearsetIndex))
        {
            // The plate has to ride along with a gear set; there is no apply without one
            reason = "No gear set is equipped. Equip one in the game's Gear Set List first.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Applies a glamour plate the way the game does: for real, on the server, permanently.
    /// </summary>
    /// <remarks>
    /// The client has no way to apply a plate on its own — the only entry point is equipping a gear
    /// set with a plate named alongside it. The gear set used is the one already equipped, so
    /// nothing about the character's actual gear or job changes and only the glamour does.
    /// <para>
    /// The plate is passed to the call rather than written onto the gear set with
    /// <c>LinkGlamourPlate</c>. Linking would permanently rewrite which plate that gear set carries,
    /// which is the player's setting and none of the wardrobe's business.
    /// </para>
    /// </remarks>
    public bool ApplyInGame(int plateId, out string message)
    {
        if (!CanApplyInGame(out var reason))
        {
            message = reason;
            return false;
        }

        var gearsets = RaptureGearsetModule.Instance();
        var gearset  = gearsets->CurrentGearsetIndex;

        // Logged before the call: if the plate numbering is ever off by one, this line is what says so
        _log.Information($"[Wardrobe] Applying glamour plate {plateId} in game " +
                         $"over gear set {gearset} ('{gearsets->GetGearset(gearset)->NameString}')");

        var result = gearsets->EquipGearset(gearset, (byte)plateId);
        if (result < 0)
        {
            message = $"The game refused to apply plate {plateId} (code {result}).";
            _log.Warning($"[Wardrobe] EquipGearset returned {result}");
            return false;
        }

        message = $"Applied glamour plate {plateId} in game.";
        return true;
    }

    /// <summary>
    /// A plate's contents as one comparable string, for telling a stored copy from the live plate.
    /// </summary>
    /// <remarks>
    /// Ordered by slot so the two sides compare equal whatever order they were built in — a stored
    /// outfit's pieces come out of a dictionary, a live plate's out of a fixed array.
    /// </remarks>
    public static string Signature(IEnumerable<PlateSlotPiece> pieces)
    {
        var sb = new StringBuilder();
        foreach (var p in pieces.OrderBy(p => (int)p.Slot))
            sb.Append((int)p.Slot).Append(':').Append(p.ItemId).Append(':')
              .Append(p.Stain1).Append(':').Append(p.Stain2).Append(';');
        return sb.ToString();
    }

    /// <summary>The same signature, taken from an outfit's stored vanilla pieces.</summary>
    public static string Signature(IReadOnlyDictionary<string, VanillaPiece> vanillaItems)
    {
        var pieces = new List<PlateSlotPiece>();
        foreach (var (slotName, piece) in vanillaItems)
        {
            if (!Enum.TryParse<EquipSlot>(slotName, out var slot)) continue;
            pieces.Add(new PlateSlotPiece(slot, piece.ItemId, piece.Stain1, piece.Stain2));
        }
        return Signature(pieces);
    }
}
