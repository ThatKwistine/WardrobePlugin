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

    // GetDesignListExtended() -> Dictionary<Guid, (DisplayName, FullPath, DisplayColor, ShownInQdb)>
    // FullPath is the design's place in Glamourer's folder tree. Verified against Glamourer.Api.dll
    // 1.6.1.7 rather than guessed: the tuple's element names come off the assembly's
    // TupleElementNamesAttribute. Absent in older Glamourer, which is why every read of it is guarded.
    private readonly ICallGateSubscriber<Dictionary<Guid, (string, string, uint, bool)>> _getDesignListExtended;

    // GetDesignJObject(designId) → JObject? — the design's own data, in the same shape GetState returns
    // and the same shape a .json design file is written in. Null when the design does not exist.
    private readonly ICallGateSubscriber<Guid, JObject?> _getDesignJObject;

    // ApiVersion.V2() → (major, minor). Throws if Glamourer is not loaded.
    private readonly ICallGateSubscriber<(int Major, int Minor)> _apiVersion;

    // OpenActorIndex(objectIndex) → void — opens Glamourer's main window on the Actors tab with that
    // actor selected. Typed with a trailing object? like the other actions, since a subscriber's
    // last type argument is the return type and InvokeAction ignores it.
    private readonly ICallGateSubscriber<int, object?> _openActorIndex;

    private const ulong HatStateFlag      = 0x02uL;
    private const ulong WeaponStateFlag   = 0x08uL;
    private const ulong CustomizationFlag = 0x04uL;
    private const ulong EquipmentFlag     = 0x02uL;

    // Property names Glamourer might use inside its Customize block. The exact shape is not
    // documented, so several spellings are tried and the real keys are logged on a miss.
    private static readonly string[] HairKeys   = { "Hairstyle", "HairStyle", "Hair" };
    private static readonly string[] RaceKeys   = { "Race" };
    private static readonly string[] GenderKeys = { "Gender", "Sex" };
    private static readonly string[] ClanKeys   = { "Clan", "Tribe", "SubRace", "Subrace" };

    // Which Customize lookups have already reported a miss. Per field rather than one flag for all of
    // them: a missing race key must not be what stops a missing hairstyle key from ever being logged.
    private readonly HashSet<string> _loggedCustomizeMisses = new(StringComparer.OrdinalIgnoreCase);

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
        _getDesignJObject = pi.GetIpcSubscriber<Guid, JObject?>("Glamourer.GetDesignJObject");
        _getDesignListExtended =
            pi.GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended");
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

        return FindCustomizeToken(customize, HairKeys, "hairstyle");
    }

    /// <summary>
    /// One value out of a Glamourer Customize block, by whichever of several spellings is present.
    /// </summary>
    /// <remarks>
    /// Handles both a bare number and the wrapped <c>{ "Value": n, "Apply": bool }</c> that Glamourer
    /// actually writes. <c>Apply</c> is deliberately ignored: it says whether a design would set the
    /// field, which is a question about a design and not about the character being looked at. In a
    /// live actor state the value is what the character currently is.
    /// </remarks>
    private JToken? FindCustomizeToken(JObject customize, string[] keys, string what)
    {
        foreach (var key in keys)
        {
            if (customize[key] is not { } token) continue;
            return token is JObject wrapped ? wrapped["Value"] : token;
        }

        if (_loggedCustomizeMisses.Add(what))
            _log.Warning($"[Wardrobe] No {what} key in Glamourer Customize. Keys present: " +
                         $"{string.Join(", ", customize.Properties().Select(p => p.Name))}");
        return null;
    }

    /// <summary>An integer out of a Glamourer Customize block, or null when it is not there.</summary>
    private int? CustomizeInt(JObject customize, string[] keys, string what) =>
        FindCustomizeToken(customize, keys, what) is { Type: JTokenType.Integer } token
            ? token.Value<int>()
            : null;

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
    /// <see cref="GetDesigns"/> held briefly, for callers that ask every frame.
    /// </summary>
    /// <remarks>
    /// The outfits grid compares its design outfits against Glamourer's list while it draws, the way
    /// the glamour-plate bar compares against the game's plates, and an IPC call per frame for a list
    /// that changes only when the user edits a design in Glamourer is waste. Half a second, matching
    /// <see cref="Services.GlamourPlateService"/>, so a design added or renamed shows up as promptly
    /// as a plate does. <see cref="InvalidateDesigns"/> drops it where the answer must be current.
    /// </remarks>
    private IList<(Guid Id, string Name)> _designCache = Array.Empty<(Guid, string)>();
    private DateTime                      _designCachedAt = DateTime.MinValue;
    private const int                     DesignCacheMs = 500;

    /// <inheritdoc cref="GetDesigns"/>
    public IList<(Guid Id, string Name)> GetDesignsCached()
    {
        if ((DateTime.UtcNow - _designCachedAt).TotalMilliseconds < DesignCacheMs) return _designCache;

        _designCache    = GetDesigns();
        _designCachedAt = DateTime.UtcNow;
        return _designCache;
    }

    /// <summary>Drops the design caches, so the next reads go to Glamourer.</summary>
    public void InvalidateDesigns()
    {
        _designCachedAt = DateTime.MinValue;
        _designContents.Clear();
    }

    // ── Design folders ────────────────────────────────────────────────────────

    /// <summary>
    /// Where each design sits in Glamourer's folder tree, as a path with the design's own name
    /// removed. Designs at the root, and every design if Glamourer is too old to answer, are absent.
    /// </summary>
    /// <remarks>
    /// <c>GetDesignListExtended</c> hands back the design's <c>FullPath</c> in the same
    /// slash-separated form Glamourer's own tree shows — the same convention Penumbra's mod tree
    /// uses, since both are the same filesystem underneath. That is also the separator
    /// <see cref="Ui.TagTree"/> nests on, so a path drops into the wardrobe's tags without being
    /// translated at all.
    /// <para>
    /// The leaf is stripped by comparing it against the design's display name rather than by simply
    /// dropping the last segment. Both would be right if <c>FullPath</c> always ends in the design's
    /// name, which is what the underlying filesystem does — but a wrong guess there would turn every
    /// design's own name into a tag, which is exactly the mess this is meant to avoid. Comparing is
    /// correct under either convention, and costs a string equality.
    /// </para>
    /// <para>
    /// Not cached. It is read when tags are imported and at no other time, so a stale answer would be
    /// worse than a call.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<Guid, string> GetDesignFolders()
    {
        var folders = new Dictionary<Guid, string>();

        try
        {
            foreach (var (id, data) in _getDesignListExtended.InvokeFunc())
            {
                var (displayName, fullPath, _, _) = data;
                if (string.IsNullOrWhiteSpace(fullPath)) continue;

                var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segments.Length == 0) continue;

                // See the note above — the leaf goes only when it is the design itself
                var end = segments.Length;
                if (end > 0 && string.Equals(segments[end - 1], displayName, StringComparison.Ordinal))
                    end--;

                if (end <= 0) continue;

                folders[id] = string.Join('/', segments[..end]);
            }
        }
        catch (Exception ex)
        {
            // Older Glamourer has GetDesignList but not the extended form. Nothing is wrong; there
            // are simply no folders to read, and every caller treats an empty map as "none".
            _log.Debug(ex, "[Wardrobe] Glamourer GetDesignListExtended unavailable — no design folders read");
        }

        return folders;
    }

    /// <summary>The tags set on a design in Glamourer, or empty when it has none.</summary>
    /// <remarks>
    /// Read straight from <c>GetDesignJObject</c> rather than through
    /// <see cref="GetDesignContents"/>, which is budgeted to a few reads a frame for the panel that
    /// draws design contents. Tag importing is a deliberate one-off over every design at once, and
    /// has no business competing with that budget or filling its cache.
    /// </remarks>
    public IReadOnlyList<string> GetDesignTags(Guid designId)
    {
        try
        {
            if (_getDesignJObject.InvokeFunc(designId) is not { } json) return Array.Empty<string>();
            if (json["Tags"] is not JArray tags) return Array.Empty<string>();

            return tags.Select(t => t.Value<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, $"[Wardrobe] Glamourer design tags unavailable for {designId}");
            return Array.Empty<string>();
        }
    }

    // ── Design contents ───────────────────────────────────────────────────────

    /// <summary>One piece a Glamourer design puts in a slot.</summary>
    /// <remarks>
    /// Only slots the design actually applies. A design holds an item for every slot whether it means
    /// to set it or not — the per-slot <c>Apply</c> flag is what separates "this design puts a hat on
    /// you" from "this design has a hat recorded and ignores it".
    /// </remarks>
    public sealed record DesignPiece(EquipSlot Slot, ulong ItemId, string Name, byte Stain1, byte Stain2);

    /// <summary>What a design does, as much of it as the wardrobe shows.</summary>
    /// <param name="Pieces">The slots it applies, in slot order.</param>
    /// <param name="AppliesEquipment">
    /// False for a design that sets no equipment at all — one saved for a face, a body or colouring.
    /// Those are common and worth saying out loud, because a card showing no pieces otherwise reads as
    /// a design that failed to load.
    /// </param>
    /// <param name="AppliesCustomize">True when it sets any part of the character's appearance.</param>
    /// <remarks>
    /// <paramref name="AppliesEquipment"/> is null when it cannot be told: a design saved on a non-human
    /// form has its equipment written as one packed array rather than as named slots, and reading that
    /// as "sets no equipment" would put the wrong label on a card. Not the same as the whole read being
    /// unavailable, which is a null <see cref="DesignContents"/>.
    /// </remarks>
    /// <param name="Customize">
    /// Every customisation value the design will actually write, keyed by Glamourer's own name for it
    /// — <c>Face</c>, <c>TailShape</c>, <c>Hairstyle</c> and the rest.
    /// </param>
    /// <remarks>
    /// Glamourer writes each entry as <c>{ "Value": n, "Apply": bool }</c>, checked against real
    /// design files rather than guessed. Only the entries whose Apply is true are kept: a value the
    /// design will not write says nothing about what the character ends up with, and treating it as a
    /// promise is how a check ends up warning about something that was never going to happen.
    /// </remarks>
    public sealed record DesignContents(
        IReadOnlyList<DesignPiece> Pieces, bool? AppliesEquipment, bool AppliesCustomize,
        IReadOnlyDictionary<string, ushort> Customize)
    {
        /// <summary>The value this design would write for a customisation, or null if it writes none.</summary>
        public ushort? Value(string key) =>
            Customize.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>
    /// The slot names Glamourer writes in a design's Equipment block, mapped to the wardrobe's slots.
    /// </summary>
    /// <remarks>
    /// Glamourer uses Penumbra's <c>EquipSlot</c> enum member names, which differ from the wardrobe's in
    /// two places: the rings are <c>RFinger</c> and <c>LFinger</c>. Taken from
    /// <c>DesignBase.SerializeEquipment</c> in Glamourer 1.6.1.7 rather than guessed — the block also
    /// holds Hat, VieraEars, Visor and Weapon, which are visibility toggles rather than slots and are
    /// deliberately absent from this map.
    /// </remarks>
    private static readonly (string Key, EquipSlot Slot)[] DesignSlotKeys =
    {
        ("MainHand", EquipSlot.MainHand), ("OffHand", EquipSlot.OffHand),
        ("Head", EquipSlot.Head), ("Body", EquipSlot.Body), ("Hands", EquipSlot.Hands),
        ("Legs", EquipSlot.Legs), ("Feet", EquipSlot.Feet),
        ("Ears", EquipSlot.Ears), ("Neck", EquipSlot.Neck), ("Wrists", EquipSlot.Wrists),
        ("RFinger", EquipSlot.RingRight), ("LFinger", EquipSlot.RingLeft),
    };

    /// <summary>
    /// Item ids Glamourer uses for "nothing" and "smallclothes", which are not game items at all.
    /// </summary>
    /// <remarks>
    /// Three bands, from <c>ItemManager</c> in Glamourer 1.6.1.7, each counting down from a base 128
    /// below the last:
    /// <list type="bullet">
    /// <item><c>NothingId(EquipSlot slot)</c> = <c>4294967167 - slot</c></item>
    /// <item><c>SmallclothesId(EquipSlot slot)</c> = <c>4294967039 - slot</c></item>
    /// <item><c>NothingId(FullEquipType type)</c> = <c>4294966911 - type</c> — the weapon slots, which
    /// key off the equip type rather than the slot and so land in a band of their own</item>
    /// </list>
    /// Because the bases are exactly 128 apart and neither enum has anything like 128 members, the bands
    /// partition cleanly and a simple threshold per band is exact. Looked up in the item sheet these
    /// resolve to nothing, which is how an empty off-hand came out as "Item 4294966911" — that is
    /// <c>NothingId</c> for equip type 0, and the reason this is a band check rather than a list.
    /// </remarks>
    private const ulong NothingBase       = 4294967167uL;
    private const ulong SmallclothesBase  = 4294967039uL;
    private const ulong WeaponNothingBase = 4294966911uL;
    private const ulong SentinelBand      = 128uL;

    /// <summary>Parsed design contents, by design id.</summary>
    private readonly Dictionary<Guid, DesignContents?> _designContents = new();

    /// <summary>When each design was last read, for expiring the entries above.</summary>
    private readonly Dictionary<Guid, DateTime> _designContentsRead = new();

    /// <summary>
    /// How long a parsed design is trusted, and how many may be read in one frame.
    /// </summary>
    /// <remarks>
    /// A design's contents change when the user edits it in Glamourer, which nothing notifies us of, so
    /// they are re-read on a timer rather than cached forever. The budget is what keeps that affordable:
    /// the outfits grid asks for every card it draws, and a wardrobe linked to sixty designs would
    /// otherwise serialise sixty designs inside Glamourer in a single frame every time the timer came
    /// round. Four per frame spreads that over a second or so, which nobody can see.
    /// </remarks>
    private const int DesignContentsTtlSeconds = 10;
    private const int DesignReadsPerFrame      = 4;

    private DateTime _readBudgetWindow;
    private int      _readBudgetUsed;

    /// <summary>
    /// What a design contains, or null while that is not known yet.
    /// </summary>
    /// <remarks>
    /// Null means "not read", never "empty" — a design whose read has not come round yet, or whose read
    /// failed, must not be shown as a design that applies nothing. Callers have to treat the two
    /// differently, because the second is a real and common kind of design and the first is a blank.
    /// </remarks>
    public DesignContents? GetDesignContents(Guid designId)
    {
        var fresh = _designContentsRead.TryGetValue(designId, out var read)
                 && (DateTime.UtcNow - read).TotalSeconds < DesignContentsTtlSeconds;

        if (fresh) return _designContents.GetValueOrDefault(designId);

        // Over budget for this frame: serve what is there, however old. A stale piece list is a far
        // better answer than a card that flickers between showing pieces and showing none.
        if (!TakeReadBudget()) return _designContents.GetValueOrDefault(designId);

        var contents = ReadDesignContents(designId);
        _designContents[designId]     = contents;
        _designContentsRead[designId] = DateTime.UtcNow;
        return contents;
    }

    /// <summary>Whether another design may be read in the current frame.</summary>
    /// <remarks>
    /// The window is a frame's worth of milliseconds rather than a real frame boundary, which this class
    /// has no access to. Slightly generous at high frame rates and slightly mean at low ones, and either
    /// way the effect is only how quickly a changed design catches up.
    /// </remarks>
    private bool TakeReadBudget()
    {
        var now = DateTime.UtcNow;
        if ((now - _readBudgetWindow).TotalMilliseconds > 15)
        {
            _readBudgetWindow = now;
            _readBudgetUsed   = 0;
        }

        return _readBudgetUsed++ < DesignReadsPerFrame;
    }

    private DesignContents? ReadDesignContents(Guid designId)
    {
        JObject? json;
        try
        {
            json = _getDesignJObject.InvokeFunc(designId);
        }
        catch (Exception ex)
        {
            // Debug rather than warning: an older Glamourer without this endpoint would otherwise fill
            // the log with one line per design per ten seconds, and the wardrobe copes without it
            _log.Debug(ex, "[Wardrobe] Glamourer GetDesignJObject failed");
            return null;
        }

        if (json == null) return null;

        var pieces  = new List<DesignPiece>();
        bool? equips = false;

        if (json["Equipment"] is JObject equipment)
        {
            // A design saved on a non-human form writes its equipment as one packed base64 array
            // instead of named slots — see DesignBase.SerializeEquipment. Nothing can be listed from
            // that, and calling it "no gear" would be a confident wrong answer.
            if (equipment["Array"] != null)
            {
                equips = null;
            }
            else
            {
                foreach (var (key, slot) in DesignSlotKeys)
                {
                    if (equipment[key] is not JObject entry) continue;
                    if (entry["Apply"]?.Value<bool>() != true) continue;

                    var itemId = entry["ItemId"]?.Value<ulong>() ?? 0uL;
                    pieces.Add(new DesignPiece(slot, itemId, DesignItemName(itemId),
                        entry["Stain"]?.Value<byte>()  ?? 0,
                        entry["Stain2"]?.Value<byte>() ?? 0));
                }

                equips = pieces.Count > 0;
            }
        }

        // Every customise value Glamourer writes carries its own Apply flag, so "does this design touch
        // the character's appearance" is whether any of them is set
        var customizeBlock = json["Customize"] as JObject;
        var customize = customizeBlock is not null
                     && customizeBlock.Properties().Any(p => p.Value is JObject c && c["Apply"]?.Value<bool>() == true);

        var values = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in customizeBlock?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            if (prop.Value is not JObject entry) continue;
            if (entry["Apply"]?.Value<bool>() != true) continue;
            if (entry["Value"] is { Type: JTokenType.Integer } value)
                values[prop.Name] = value.Value<ushort>();
        }

        return new DesignContents(pieces, equips, customize, values);
    }

    /// <summary>An item id as a design means it, including the ids that are not items.</summary>
    private static string DesignItemName(ulong itemId)
    {
        if (itemId > NothingBase      - SentinelBand) return "Nothing";
        if (itemId > SmallclothesBase - SentinelBand) return "Smallclothes";

        // Weapons say which hand is empty, because "Nothing" against Off Hand reads as a mistake where
        // "No off-hand" reads as a two-handed weapon, which is what it usually is
        if (itemId > WeaponNothingBase - SentinelBand) return "None";

        var name = Plugin.ItemLookup.GetItemName(itemId);
        return string.IsNullOrEmpty(name) ? $"Item {itemId}" : name;
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
    /// <para>
    /// Glamourer is asked first, and the game object is only the fallback. A character changed to
    /// another race in Glamourer keeps its original race in the game object — Glamourer redraws the
    /// model without writing back — so reading the game object alone reported the race the player
    /// used to be. A hair mod built for the race they now are was then rejected as unsupported, which
    /// is issue #19: a Miqo'te-only hair on a Viera character wearing a Miqo'te appearance.
    /// </para>
    /// </remarks>
    public int? GetPlayerRaceCode()
    {
        var native = RaceCodeFromGameObject();

        if (RaceCodeFromGlamourer() is not { } applied)
            return native;

        // Worth having in /xllog when someone reports a hair being refused: it names the race the
        // character is being drawn as and the race the game still thinks it is, which is the whole
        // of what went wrong in issue #19 and is otherwise invisible from a screenshot of the miss.
        if (native != applied)
            _log.Debug($"[Wardrobe] Race code {applied:D4} from Glamourer; the game object still " +
                       $"says {native:D4}. Using Glamourer's.");

        return applied;
    }

    /// <summary>The race code of the character the game itself thinks the player is.</summary>
    private int? RaceCodeFromGameObject()
    {
        var player = _objects.LocalPlayer;
        if (player == null) return null;

        try
        {
            var customize = player.Customize;
            if (customize.Length < 5) return null;

            return RaceCode(customize[0], customize[1], customize[4]);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Could not read player customise data");
            return null;
        }
    }

    /// <summary>
    /// The race code of the appearance Glamourer is currently applying, or null when it cannot say.
    /// </summary>
    /// <remarks>
    /// The Customize block holds the raw customise bytes, in the same encoding the game object uses —
    /// race 1-8, gender 0 male and 1 female, and a clan running two per race, so Viera are 15 Rava
    /// and 16 Veena. Verified against Glamourer's own saved designs rather than assumed: a stored
    /// design for a Viera character reads Race 8, Clan 16. Both paths therefore share
    /// <see cref="RaceCode"/> and there is one mapping to be wrong about.
    /// </remarks>
    private int? RaceCodeFromGlamourer()
    {
        if (_objects.LocalPlayer == null) return null;

        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state?["Customize"] is not JObject customize) return null;

            var race   = CustomizeInt(customize, RaceKeys,   "race");
            var gender = CustomizeInt(customize, GenderKeys, "gender");
            if (race is null || gender is null) return null;

            // Only Hyur need it, and a missing clan should not cost the whole answer for everyone else
            var clan = CustomizeInt(customize, ClanKeys, "clan") ?? 0;

            return RaceCode(race.Value, gender.Value, clan);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer race lookup failed");
            return null;
        }
    }

    /// <summary>Raw race, gender and clan bytes to the model race code they name.</summary>
    private static int? RaceCode(int race, int gender, int clan)
    {
        var female = gender == 1;

        return race switch
        {
            1 => clan == 2 ? (female ? 401 : 301)   // Hyur: Highlander
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

    /// <summary>
    /// Whether the character's hat is being shown, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Read from the known path rather than by searching: <c>Equipment.Hat.Show</c>, which is where
    /// <c>DesignBase.SerializeEquipment</c> writes it and so where <c>GetState</c> returns it. The
    /// search below is kept as a fallback in case that moves, which is how the weapon's was written
    /// before the layout was confirmed against Glamourer's own source.
    /// </remarks>
    public bool? GetHatVisible()
    {
        if (_objects.LocalPlayer == null) return null;
        try
        {
            var (ec, state) = _getState.InvokeFunc(PlayerIndex, 0u);
            if (ec != 0 || state == null) return null;

            if (state["Equipment"]?["Hat"]?["Show"] is { Type: JTokenType.Boolean } show)
                return show.Value<bool>();

            return FindVisibility(state, "hat");
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Wardrobe] Glamourer GetHatVisible failed");
            return null;
        }
    }

    /// <summary>
    /// Shows or hides the character's hat.
    /// </summary>
    /// <remarks>
    /// A screenshot session turns this on for a head piece: a hat hidden in Glamourer photographs as a
    /// bare head, and a session that quietly produced twelve pictures of no hat would be worse than one
    /// that refused. Everything else in the session leaves it as the user had it.
    /// </remarks>
    public bool SetHatVisible(bool visible)
    {
        if (_objects.LocalPlayer == null) return false;
        try
        {
            var ec = _setMetaState.InvokeFunc(PlayerIndex, HatStateFlag, visible, 0u, 0uL);
            _log.Debug($"[Wardrobe] Glamourer SetHatVisible={visible} → ec={ec}");
            return ec == 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Glamourer SetMetaState (hat) failed");
            return false;
        }
    }

    private static bool? FindWeaponVisibility(JToken node) => FindVisibility(node, "weapon");

    private static bool? FindVisibility(JToken node, string needle)
    {
        if (node is not JObject obj) return null;

        foreach (var prop in obj.Properties())
        {
            if (prop.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.Type == JTokenType.Boolean)
                    return prop.Value.Value<bool>();

                if (prop.Value is JObject inner)
                {
                    if (inner["Show"]  is { Type: JTokenType.Boolean } show)  return show.Value<bool>();
                    if (inner["Value"] is { Type: JTokenType.Boolean } value) return value.Value<bool>();
                }
            }

            var nested = FindVisibility(prop.Value, needle);
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

    /// <summary>
    /// The customisation a mod for this slot depends on, named as Glamourer names it, or null where
    /// the slot has none to depend on.
    /// </summary>
    /// <remarks>
    /// A mod replaces the files of one numbered variant — face 3, tail 2 — so it is invisible unless
    /// the character is set to that number, and this is the value that has to agree with it.
    /// <para>
    /// Tail and Viera ears share <c>TailShape</c> because the game does: one customisation byte is
    /// the tail on races that have one and the ears on Viera. Confirmed against
    /// <c>Glamourer.GameData.CustomizeSet</c> in 1.6.1.7, where <c>CustomizeIndex.TailShape</c> reads
    /// from a list named <c>TailEarShapes</c>, rather than assumed from the two being alike.
    /// </para>
    /// <para>
    /// Null for Skin, which has no customisation behind it at all — the body id in a skin mod's paths
    /// is <c>b0001</c> for every player character, and <c>BodyType</c> is a different thing. Null for
    /// Hair as well, though for the opposite reason: its number is not left to a design to get right,
    /// since wearing a hair mod sets the hairstyle from the mod itself. Both are still checked against
    /// the race, which is the failure they really do have.
    /// </para>
    /// </remarks>
    public static string? ToCustomizeKey(EquipSlot slot) => slot switch
    {
        EquipSlot.Face      => "Face",
        EquipSlot.Tail      => "TailShape",
        EquipSlot.VieraEars => "TailShape",
        _                   => null,
    };

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

    /// <summary>
    /// Empties a slot properly — Glamourer's "Nothing", not an invisible item sitting in the slot.
    /// </summary>
    /// <remarks>
    /// The wardrobe's usual way of clearing a slot is the Emperor's New item, which is invisible but
    /// is still an item: Glamourer shows the slot as filled, and a mod that redirects Emperor's New
    /// still draws. "Nothing" is the slot actually being empty, which is what someone asking for a
    /// bare character means.
    /// <para>
    /// The id comes from the sentinel band documented above: <c>NothingId(slot)</c> is
    /// <see cref="NothingBase"/> minus the slot, and the slot in that formula is the same enum
    /// <see cref="ToApiEquipSlot"/> returns — which is why this can be computed rather than looked up,
    /// and why <see cref="DesignItemName"/> reads back exactly what this writes.
    /// </para>
    /// <para>
    /// Equipment only. The weapon slots key their empty off the equip type rather than the slot and
    /// land in a different band, so they are refused here rather than sent a wrong id.
    /// </para>
    /// </remarks>
    public bool SetSlotToNothing(EquipSlot slot)
    {
        if (slot is EquipSlot.MainHand or EquipSlot.OffHand)
        {
            _log.Debug($"[Wardrobe] SetSlotToNothing: {slot} is a weapon slot — not supported");
            return false;
        }

        var apiSlot = ToApiEquipSlot(slot);
        if (apiSlot == 0)
        {
            _log.Warning($"[Wardrobe] SetSlotToNothing: no slot mapping for {slot}");
            return false;
        }

        return SetItem(slot, NothingBase - apiSlot);
    }

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
