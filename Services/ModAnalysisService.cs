using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

public enum ModGroupType { Single = 0, Multi = 1 }

/// <param name="Slots">
/// Slots the group's own files touch, which is not the same as the mod's slots — a mod covering body
/// and legs has groups that only change one of them. Empty means nothing in the group named a slot:
/// meta edits, materials and shared textures all land here, and they are assumed to matter to
/// everything rather than to nothing.
/// </param>
public record ModOptionGroup(string GroupName, ModGroupType GroupType, IReadOnlyList<string> OptionNames,
    IReadOnlySet<EquipSlot>? Slots = null)
{
    /// <summary>
    /// Whether this group is worth an opinion from an item in the given slot.
    /// </summary>
    /// <remarks>
    /// The question behind issue #12: two items from one mod, worn together, each writing the whole
    /// mod's options and so overwriting the other's. A group whose files never touch your slot is one
    /// you have no business asserting, and leaving it alone is what lets the two coexist.
    /// </remarks>
    public bool AffectsSlot(EquipSlot slot) =>
        Slots == null || Slots.Count == 0 || Slots.Contains(slot);

    /// <summary>
    /// Whether the group's own files <em>name</em> this slot, rather than merely not ruling it out.
    /// </summary>
    /// <remarks>
    /// The stricter question, and the one to ask before writing on another item's behalf.
    /// <see cref="AffectsSlot"/> answers true for a group that named no slot at all, which is right
    /// when deciding what an item may assert for itself — a colour or material group belongs to
    /// whoever is wearing it. It is wrong when copying settings onto a sibling: every such group
    /// would look like shared business, and one item's options would land on every other item from
    /// the same mod (issue #12).
    /// </remarks>
    public bool NamesSlot(EquipSlot slot) => Slots is { Count: > 0 } && Slots.Contains(slot);
}

/// <summary>Trimming a mod's option settings down to one slot's business.</summary>
public static class ModOptionSets
{
    /// <summary>
    /// Drops the groups that have nothing to do with a slot.
    /// </summary>
    /// <remarks>
    /// The heart of issue #12. Items sharing a mod across slots are worn together and Penumbra holds
    /// one option state per mod, so they were given identical settings — which meant the legs
    /// asserting the body's groups, and whichever was applied last winning. Each keeping only what
    /// its own slot is made of lets a variant in one slot survive its sibling in another.
    /// <para>
    /// A group whose files name no slot is kept for everyone: meta edits, materials and shared
    /// textures cannot be attributed, and dropping them would quietly stop applying settings that
    /// were doing their job.
    /// </para>
    /// </remarks>
    public static Dictionary<string, T> ForSlot<T>(Dictionary<string, T> settings,
        IReadOnlyList<ModOptionGroup>? groups, EquipSlot slot)
    {
        if (groups == null) return new Dictionary<string, T>(settings);

        var byName = ByName(groups);

        return settings
            .Where(kv => !byName.TryGetValue(kv.Key, out var g) || g.AffectsSlot(slot))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Copies onto another item only the groups its own slot is named in, leaving everything else
    /// it had exactly as it was.
    /// </summary>
    /// <remarks>
    /// Two items from one mod are worn together and Penumbra holds one option state per mod, so a
    /// group they are both made of has to agree between them. Everything else is none of the
    /// editor's business, and this is the difference between keeping those few groups in step and
    /// what the previous version did — replace the sibling's settings wholesale, so editing the feet
    /// rewrote the legs, the wrists and both variants of the body (issue #12, reported again after
    /// v1.5.0.0).
    /// <para>
    /// Merged rather than assigned, for the same reason: the receiving item's own groups survive.
    /// An empty value is skipped — a group the editor left alone is an abstention, and propagating
    /// one would overwrite the sibling's opinion with the absence of an opinion.
    /// </para>
    /// </remarks>
    /// <returns>The group names actually copied, for logging.</returns>
    public static IReadOnlyList<string> MergeOwned<T>(Dictionary<string, T> target,
        Dictionary<string, T> source, IReadOnlyList<ModOptionGroup>? groups, EquipSlot slot)
    {
        // No analysis means no way to tell whose group is whose. Copying everything is what caused
        // the fault, so the safe direction when we cannot tell is to copy nothing.
        if (groups == null) return Array.Empty<string>();

        var byName = ByName(groups);
        var copied = new List<string>();

        foreach (var (group, value) in source)
        {
            if (!byName.TryGetValue(group, out var g) || !g.NamesSlot(slot)) continue;
            if (value is System.Collections.ICollection { Count: 0 }) continue;

            target[group] = value;
            copied.Add(group);
        }

        return copied;
    }

    /// <summary>
    /// The groups keyed by name, with any sharing a name folded into one.
    /// </summary>
    /// <remarks>
    /// Nothing stops a mod having two groups with the same name — a real one shipped two called
    /// "3D OPTIONS" — and Penumbra keys option settings by name, so the second is indistinguishable
    /// from the first from the outside. Building the map with ToDictionary threw on the duplicate
    /// and took the whole import down mid-draw, which is a hard crash over a mod that Penumbra
    /// itself is perfectly happy with.
    /// <para>
    /// Folding them keeps the answer on the safe side of both questions this map is asked. A group
    /// naming no slot at all is unattributable, and one of the pair naming none makes the pair
    /// unattributable: <see cref="ModOptionGroup.AffectsSlot"/> then answers yes for every slot, so
    /// no setting that was doing its job is dropped, and <see cref="ModOptionGroup.NamesSlot"/>
    /// answers no, so nothing is copied onto a sibling on a guess. Otherwise the slots are unioned,
    /// because the settings under that one name really do belong to both.
    /// </para>
    /// </remarks>
    private static Dictionary<string, ModOptionGroup> ByName(IReadOnlyList<ModOptionGroup> groups)
    {
        var byName = new Dictionary<string, ModOptionGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            if (!byName.TryGetValue(g.GroupName, out var seen))
            {
                byName[g.GroupName] = g;
                continue;
            }

            byName[g.GroupName] = seen.Slots is { Count: > 0 } && g.Slots is { Count: > 0 }
                ? seen with { Slots = Union(seen.Slots, g.Slots) }
                : seen with { Slots = new HashSet<EquipSlot>() };
        }

        return byName;
    }

    private static IReadOnlySet<EquipSlot> Union(IReadOnlySet<EquipSlot> a, IReadOnlySet<EquipSlot> b)
    {
        var slots = new HashSet<EquipSlot>(a);
        slots.UnionWith(b);
        return slots;
    }
}

public record ModAnalysisResult(
    IReadOnlySet<EquipSlot> DetectedSlots,
    IReadOnlyList<ModOptionGroup> OptionGroups,
    /// <summary>Equipment set IDs extracted from the mod file paths, keyed by slot.</summary>
    IReadOnlyDictionary<EquipSlot, ushort> SlotSetIds,
    /// <summary>
    /// Hairstyle numbers keyed by model race code (0101, 1801, …). Hairstyle numbering differs
    /// per race, so the right one depends on who is wearing it.
    /// </summary>
    IReadOnlyDictionary<int, ushort> HairIdsByRace,
    /// <summary>
    /// What the mod replaces within a mod category, keyed by slot — the .pap file name for an
    /// animation, the monster id for a mount. Equipment identifies itself by set ID instead, so only
    /// mod categories ever appear here. See <see cref="Models.WardrobeItem.Replaces"/>.
    /// </summary>
    IReadOnlyDictionary<EquipSlot, string> ReplaceKeys,
    /// <summary>
    /// Slots the mod ships an actual model for, as opposed to only materials and textures.
    /// </summary>
    /// <remarks>
    /// What separates a sculpt from a retexture, and so what decides a customisation item's
    /// <see cref="Models.WardrobeItem.Layer"/>. A mod shipping both is a sculpt: the textures are
    /// its own, painted on the model beside them.
    /// </remarks>
    IReadOnlySet<EquipSlot> ModelSlots,
    /// <summary>
    /// Every customisation number the mod covers, keyed by slot and then by model race code — the
    /// face numbers of a face mod, and so on.
    /// </summary>
    /// <remarks>
    /// A set per race rather than one number, because a mod routinely covers several at once: option
    /// groups let one face mod ship f0001 through f0004, and for more than one race beside. The
    /// single number in <see cref="SlotSetIds"/> is whichever was seen first, which is fine for
    /// naming the mod and useless for asking "does this cover the face I am about to be given".
    /// </remarks>
    IReadOnlyDictionary<EquipSlot, IReadOnlyDictionary<int, IReadOnlyList<ushort>>> CustomizeCoverage
)
{
    /// <summary>Layer name for a customisation mod that reshapes the part it is on.</summary>
    public const string SculptLayer = "sculpt";

    /// <summary>Layer name for one that only repaints it.</summary>
    public const string TextureLayer = "texture";

    /// <summary>
    /// The layer an item for this slot should be given, or null when the question does not arise.
    /// </summary>
    /// <remarks>
    /// Null for everything but customisation, which is the only place two mods can be on one slot
    /// doing different jobs — and null too for a customisation slot this analysis never saw, so a
    /// hand-made item with no mod behind it is left blank rather than guessed at. Blank means the
    /// item takes the whole slot, which is what every item did before layers existed.
    /// </remarks>
    public string? LayerFor(EquipSlot slot) =>
        !slot.IsCustomization() || !DetectedSlots.Contains(slot)
            ? null
            : ModelSlots.Contains(slot) ? SculptLayer : TextureLayer;
}

public class ModAnalysisService
{
    private readonly IPluginLog? _log;

    /// <summary>Game paths seen during the current Analyze call, used to spot a layout we cannot read.</summary>
    private int _pathsSeen;

    /// <summary>
    /// Texture and material paths under <c>chara/</c> that name no part of the game — see
    /// <see cref="CustomTexturePattern"/>.
    /// </summary>
    private int _customTextures;

    /// <summary>Every customisation id seen this Analyze call, by slot then model race code.</summary>
    private readonly Dictionary<EquipSlot, Dictionary<int, SortedSet<ushort>>> _coverage = new();

    /// <summary>Slots a <c>.mdl</c> was seen for during the current Analyze call.</summary>
    /// <remarks>
    /// An instance field rather than another parameter threaded through <c>ClassifyPath</c> and
    /// <c>AddGroup</c>, which the two counters above already do for the same reason.
    /// </remarks>
    private readonly HashSet<EquipSlot> _modelSlots = new();

    public ModAnalysisService(IPluginLog? log = null) => _log = log;

    /// <summary>An id found in a path, and whether the path that gave it was a model.</summary>
    /// <remarks>
    /// Which file the id came from decides which answer wins. A hair mod ships the model of the
    /// hairstyle it replaces, and often textures belonging to entirely different hairstyles alongside —
    /// shared masks, patches for the vanilla hair it sits under. Taking whichever path was read first
    /// meant a texture could name the hairstyle, and the wardrobe would then switch the character to a
    /// hairstyle the mod does not replace: the mod enabled, the hair unchanged, and nothing on screen
    /// saying why. The model is the one file that can only belong to the hairstyle being replaced.
    /// </remarks>
    private readonly record struct Detected(ushort Id, bool FromModel);

    /// <summary>Records an id, letting a model's answer replace one a texture gave first.</summary>
    private static void Record<TKey>(Dictionary<TKey, Detected> map, TKey key, ushort id, bool fromModel)
        where TKey : notnull
    {
        // First answer wins among equals, so a mod with two models for one race keeps the earlier —
        // there is nothing to choose between them and the order is at least stable
        if (map.TryGetValue(key, out var seen) && (seen.FromModel || !fromModel)) return;

        map[key] = new Detected(id, fromModel);
    }

    /// <summary>Drops the provenance, leaving the ids the rest of the plugin works in.</summary>
    private static Dictionary<TKey, ushort> Ids<TKey>(Dictionary<TKey, Detected> map) where TKey : notnull =>
        map.ToDictionary(kv => kv.Key, kv => kv.Value.Id);

    /// <summary>Freezes the coverage gathered this call into the result's read-only shape.</summary>
    private IReadOnlyDictionary<EquipSlot, IReadOnlyDictionary<int, IReadOnlyList<ushort>>> Coverage() =>
        _coverage.ToDictionary(
            slot => slot.Key,
            slot => (IReadOnlyDictionary<int, IReadOnlyList<ushort>>)slot.Value.ToDictionary(
                race => race.Key,
                race => (IReadOnlyList<ushort>)race.Value.ToList()));

    /// <summary>Whether a game path points at a model rather than a material or texture.</summary>
    private static bool IsModelPath(string gamePath) =>
        gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    // chara/equipment/e{SetId}/{model|material|texture}/…c{race}e{SetId}_{slot}…
    // Group 1 = SetId, group 2 = slot suffix.
    //
    // Materials and textures count, not just models: plenty of mods retexture an existing gear set
    // and ship no .mdl at all, and requiring one made them look like they touched nothing.
    //   chara/equipment/e6106/model/c0201e6106_top.mdl
    //   chara/equipment/e6106/material/v0001/mt_c0201e6106_top_a.mtrl
    //   chara/equipment/e6106/texture/v01_c0201e6106_top_base_346649790.tex
    private static readonly Regex EquipPattern =
        new(@"chara/equipment/e(\d+)/(?:model|material|texture)/[^""]*?c\d+e\d+_(met|top|glv|dwn|sho)[_.]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // chara/accessory/a{SetId}/{model|material|texture}/…c{race}a{SetId}_{slot}…
    private static readonly Regex AccessoryPattern =
        new(@"chara/accessory/a(\d+)/(?:model|material|texture)/[^""]*?c\d+a\d+_(ear|nek|wrs|rir|ril)[_.]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WeaponPattern =
        new(@"chara/weapon/w(\d+)/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // chara/human/c{race}/obj/{hair|face|tail|zear|body}/...  — character customisation, not equipment.
    // Group 1 = the body-part folder, which is what identifies the kind of mod.
    // Group 1 = model race code (0101 Midlander M, 1801 Viera F, …), group 2 = body part,
    // group 3 = the numeric id. Hairstyle numbers are per-race — one mod is commonly hair 151 on
    // most races but 11 on Hrothgar and 5 on Viera — so the race code has to be kept alongside.
    private static readonly Regex CustomizationPattern =
        new(@"chara/human/c(\d+)/obj/(hair|face|tail|zear|body)/[hftzb](\d+)/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // chara/common/...  — textures shared across every character: piercings, tattoos, face paints,
    // decals. Tied to no slot at all, so they land in EquipSlot.Other.
    private static readonly Regex CommonPattern =
        new(@"chara/common/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // chara/<anything>/….tex or .mtrl that matched none of the layouts above — a path invented by
    // the mod author rather than one the game owns. They work because some other mod's material
    // points at them: a body replacer ships materials naming chara/bibo_mid_base.tex, and a skin
    // texture for it ships that file. Nothing in such a path says what it is, and Penumbra shows no
    // changed items for a mod made only of them either.
    private static readonly Regex CustomTexturePattern =
        new(@"^chara/.+\.(?:tex|mtrl)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any .pap file — emotes, poses, idles and battle animations. Animation data lives in no other
    // file type, so the extension alone identifies the mod, and the file name is what two mods must
    // share to be replacing the same animation (…/bt_common/emote/j_pose.pap → "j_pose").
    private static readonly Regex AnimationPattern =
        new(@"([^/]+)\.pap$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // chara/monster/m{id}/… and chara/demihuman/d{id}/… — mounts, minions and NPC models.
    // Group 1 = m or d, group 2 = the id; together they name what the mod replaces.
    private static readonly Regex MonsterPattern =
        new(@"chara/(?:monster|demihuman)/([md])(\d+)/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // vfx/… anywhere in the path, plus .avfx effect definitions. Covers standalone spell-effect
    // mods as well as the glow half of a weapon mod — the latter is discarded below.
    private static readonly Regex VfxPattern =
        new(@"(?:^|/)vfx/|\.avfx$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Slots that are nearly always a supporting part of some other mod rather than the point of
    /// it, and so are only reported when nothing else was detected.
    /// </summary>
    private static readonly HashSet<EquipSlot> Auxiliary = new() { EquipSlot.Other, EquipSlot.Vfx };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Pre-selects option groups from whatever Penumbra currently has active for the mod, so an
    /// import starts from the configuration the user already set up in Penumbra rather than from
    /// each group's first option.
    /// </summary>
    /// <remarks>
    /// Only groups Penumbra actually reports are touched; the rest keep whatever the caller had,
    /// which is each group's default. Option names are taken from the analysed group rather than
    /// from Penumbra's reply, so a difference in casing cannot store a name that will not match
    /// later. Anything Penumbra reports that the group does not list is dropped: a stale name from
    /// a mod that has since been reorganised must not end up saved on a wardrobe item.
    /// </remarks>
    public static void SeedSelectionsFromPenumbra(
        IReadOnlyList<ModOptionGroup> groups,
        Dictionary<string, List<string>> live,
        Dictionary<string, int> singleSel,
        Dictionary<string, HashSet<string>> multiSel)
    {
        if (live.Count == 0) return;

        foreach (var g in groups)
        {
            if (!live.TryGetValue(g.GroupName, out var active) || active.Count == 0) continue;

            if (g.GroupType == ModGroupType.Single)
            {
                for (var i = 0; i < g.OptionNames.Count; i++)
                {
                    if (!g.OptionNames[i].Equals(active[0], StringComparison.OrdinalIgnoreCase)) continue;
                    singleSel[g.GroupName] = i;
                    break;
                }
            }
            else
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in g.OptionNames)
                    if (active.Contains(name, StringComparer.OrdinalIgnoreCase))
                        set.Add(name);
                multiSel[g.GroupName] = set;
            }
        }
    }

    /// <summary>
    /// Reads the Penumbra mod folder and returns which equipment slots it touches
    /// plus any option groups and their option names.
    /// </summary>
    public ModAnalysisResult Analyze(string modFolderPath)
    {
        _pathsSeen      = 0;
        _customTextures = 0;
        _modelSlots.Clear();
        _coverage.Clear();
        var slots   = new HashSet<EquipSlot>();
        var setIds  = new Dictionary<EquipSlot, Detected>();
        var hairIds = new Dictionary<int, Detected>();
        var replace = new Dictionary<EquipSlot, string>();
        var groups  = new List<ModOptionGroup>();

        if (!Directory.Exists(modFolderPath))
            return new ModAnalysisResult(slots, groups, Ids(setIds), Ids(hairIds), replace,
                new HashSet<EquipSlot>(_modelSlots), Coverage());

        var meta = ReadMeta(Path.Combine(modFolderPath, "meta.json"));

        // default_mod.json
        var defaultFile = Path.Combine(modFolderPath, "default_mod.json");
        if (File.Exists(defaultFile))
            foreach (var key in ReadFileKeys(defaultFile))
                ClassifyPath(key, slots, setIds, hairIds, replace);

        // FileVersion 4 moved the default option's files into meta.json as well, so a mod written
        // in that layout has no default_mod.json at all. Everything a mod ships outside an option
        // group lives here — for a mod with no groups, that is the entire mod, which is why one
        // could be read as touching no slots while Penumbra listed its changed items quite happily.
        foreach (var key in PathKeys(meta?.DefaultData))
            ClassifyPath(key, slots, setIds, hairIds, replace);

        // group_NNN_*.json — the older layout, one file per group
        foreach (var groupFile in Directory.GetFiles(modFolderPath, "group_*.json").OrderBy(x => x))
        {
            var g = TryReadGroup(groupFile);
            if (g is null) continue;
            AddGroup(g, slots, setIds, hairIds, replace, groups);
        }

        // meta.json — FileVersion 4 and later put every group inside meta.json instead, with no
        // default_mod.json and no group files at all. Reading only the old layout found nothing
        // for such mods, so they appeared to touch no slots whatsoever.
        foreach (var g in meta?.Groups ?? new List<GroupJson>())
            AddGroup(g, slots, setIds, hairIds, replace, groups);

        foreach (var key in ProteusPaths(modFolderPath))
            ClassifyPath(key, slots, setIds, hairIds, replace);

        // Penumbra's on-disk format is barely documented and has changed once already. A mod that
        // yields no game paths at all is nearly always a layout this parser does not understand
        // rather than a genuinely empty mod, and without this it fails silently — the import panel
        // just says no slots were detected, which looks like a normal outcome.
        if (_pathsSeen == 0)
            _log?.Warning($"[Wardrobe] Analysed '{Path.GetFileName(modFolderPath)}' " +
                          $"(meta FileVersion {meta?.FileVersion.ToString() ?? "none"}) and found no game " +
                          $"paths at all. If Penumbra shows changed items for it, this parser does not " +
                          $"understand its layout — please report it.");
        else
            _log?.Debug($"[Wardrobe] Analysed '{Path.GetFileName(modFolderPath)}' " +
                        $"(meta FileVersion {meta?.FileVersion.ToString() ?? "none"}): " +
                        $"{_pathsSeen} path(s), {groups.Count} group(s), {slots.Count} slot(s)");

        slots.Remove(EquipSlot.Unknown);

        // chara/common files and VFX are usually supporting assets for a real mod — a shared ID
        // texture shipped alongside an accessory model, a glow shipped with a weapon — rather than
        // the point of it. Only treat them as a category in their own right when nothing else was
        // found, or every such mod gains a phantom extra slot.
        if (slots.Any(s => !Auxiliary.Contains(s)))
            slots.ExceptWith(Auxiliary);

        // A mod whose every path is one the author invented, with nothing recognisable beside it,
        // is in practice a skin: body textures for Bibo+, YAB, Gen3 and the rest, plus the tattoos,
        // scars and body writing that ride on the same redirection. Nothing in the paths proves it,
        // which is why this waits until everything else has come up empty — but the alternative is
        // reporting that a mod full of files changes nothing at all, and the slot can be changed
        // after import like any other.
        if (slots.Count == 0 && _customTextures > 0)
        {
            slots.Add(EquipSlot.Skin);
            _log?.Information($"[Wardrobe] '{Path.GetFileName(modFolderPath)}' names no part of the " +
                              $"game in any of its {_pathsSeen} path(s), but {_customTextures} are " +
                              $"custom textures — taking it for a skin.");
        }

        return new ModAnalysisResult(slots, groups, Ids(setIds), Ids(hairIds), replace,
                new HashSet<EquipSlot>(_modelSlots), Coverage());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a Penumbra group Type string onto how the group should be presented.
    /// </summary>
    /// <remarks>
    /// Only "Single" is a one-of-N dropdown. "Multi" is checkboxes, and "Imc" is *also* checkboxes —
    /// an IMC group stores a bitmask (its DefaultSettings is an integer and each option carries a
    /// power-of-two AttributeMask), so every option toggles independently. Treating unrecognised
    /// types as Single silently renders them as a dropdown and lets only one option be applied,
    /// so anything unknown is logged rather than quietly mishandled.
    /// </remarks>
    private ModGroupType ClassifyGroupType(string type, string groupName)
    {
        switch (type.ToLowerInvariant())
        {
            case "single":
                return ModGroupType.Single;
            case "multi":
            case "imc":
            case "combining":
                return ModGroupType.Multi;
            default:
                _log?.Warning($"[Wardrobe] Unknown Penumbra group type '{type}' for group " +
                              $"'{groupName}' — treating as single-select, which may be wrong.");
                return ModGroupType.Single;
        }
    }

    /// <summary>
    /// Game paths from a Proteus project, if the mod is one.
    /// </summary>
    /// <remarks>
    /// Proteus is a separate tool for layering skin and gear textures. A mod built with it carries
    /// its own <c>Proteus/metadata.json</c>, and its Penumbra groups list option names with no files
    /// under them at all — the files are generated when the option is applied. Read as Penumbra
    /// alone, such a mod has option groups full of options that change nothing, and the wardrobe
    /// reported it as touching no slots.
    /// <para>
    /// Every path is found by walking for <c>MaterialGamePath</c> rather than by following the
    /// structure, which nests differently for a plain overlay than for one inside an option group,
    /// and holds either a single path or a list of them. This is a third party's format with no
    /// spec to hand, so the shallowest possible reading of it is the one most likely to survive its
    /// next version.
    /// </para>
    /// </remarks>
    private IEnumerable<string> ProteusPaths(string modFolderPath)
    {
        var file = Path.Combine(modFolderPath, "Proteus", "metadata.json");
        if (!File.Exists(file)) return Array.Empty<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (Exception ex)
        {
            _log?.Warning($"[Wardrobe] Could not read {file}: {ex.Message}");
            return Array.Empty<string>();
        }

        var found = new List<string>();
        using (doc)
            CollectMaterialPaths(doc.RootElement, found);

        if (found.Count > 0)
            _log?.Debug($"[Wardrobe] '{Path.GetFileName(modFolderPath)}' is a Proteus mod: " +
                        $"{found.Count} path(s) read from its metadata.");

        return found;
    }

    /// <summary>Every <c>MaterialGamePath</c> anywhere below this element, string or array alike.</summary>
    private static void CollectMaterialPaths(JsonElement element, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("MaterialGamePath"))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            if (prop.Value.GetString() is { Length: > 0 } one) found.Add(one);
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                                if (item.ValueKind == JsonValueKind.String &&
                                    item.GetString() is { Length: > 0 } many)
                                    found.Add(many);
                        }
                        continue;
                    }

                    CollectMaterialPaths(prop.Value, found);
                }
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectMaterialPaths(item, found);
                return;
        }
    }

    /// <summary>
    /// The game paths one option (or the mod's default data) changes.
    /// </summary>
    /// <remarks>
    /// Swaps count alongside plain files: a swap redirects one game path to another, and the key —
    /// the path being replaced — says what the mod changes just as a file does. A mod built entirely
    /// out of swaps ships no files at all, and reading only Files made it look empty.
    /// </remarks>
    private static IEnumerable<string> PathKeys(OptionJson? option)
    {
        if (option == null) yield break;

        foreach (var map in new[] { option.Files, option.FileSwaps })
        {
            if (map == null) continue;
            foreach (var key in map.Keys)
                yield return key;
        }
    }

    private static IEnumerable<string> ReadFileKeys(string jsonPath)
    {
        string text;
        try { text = File.ReadAllText(jsonPath); }
        catch { yield break; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch { yield break; }

        using (doc)
        {
            foreach (var section in new[] { "Files", "FileSwaps" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var files) ||
                    files.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var prop in files.EnumerateObject())
                    yield return prop.Name;
            }
        }
    }

    private static GroupJson? TryReadGroup(string jsonPath)
    {
        try { return JsonSerializer.Deserialize<GroupJson>(File.ReadAllText(jsonPath), JsonOpts); }
        catch { return null; }
    }

    /// <summary>
    /// meta.json, which from FileVersion 4 also carries the option groups.
    /// </summary>
    /// <remarks>
    /// Unknown fields are ignored rather than treated as an error, so a future format revision
    /// degrades to reading whatever it still recognises instead of failing outright.
    /// </remarks>
    private MetaJson? ReadMeta(string metaPath)
    {
        if (!File.Exists(metaPath)) return null;

        try
        {
            return JsonSerializer.Deserialize<MetaJson>(File.ReadAllText(metaPath), JsonOpts);
        }
        catch (Exception ex)
        {
            _log?.Warning($"[Wardrobe] Could not read {metaPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Records a group's option names and classifies every game path it references.</summary>
    private void AddGroup(GroupJson g, HashSet<EquipSlot> slots, Dictionary<EquipSlot, Detected> setIds,
        Dictionary<int, Detected> hairIds, Dictionary<EquipSlot, string> replace, List<ModOptionGroup> groups)
    {
        var optNames = new List<string>();

        // Collected for this group alone and then folded into the mod's set, so a group can be asked
        // which slots it changes. Everything else classification produces stays mod-wide.
        var groupSlots = new HashSet<EquipSlot>();

        foreach (var opt in g.Options)
        {
            optNames.Add(opt.Name);

            foreach (var key in PathKeys(opt))
                ClassifyPath(key, groupSlots, setIds, hairIds, replace);
        }

        groupSlots.Remove(EquipSlot.Unknown);
        slots.UnionWith(groupSlots);

        if (optNames.Count > 0)
            groups.Add(new ModOptionGroup(g.Name, ClassifyGroupType(g.Type, g.Name), optNames, groupSlots));
    }

    private void ClassifyPath(string gamePath, HashSet<EquipSlot> slots, Dictionary<EquipSlot, Detected> setIds,
        Dictionary<int, Detected> hairIds, Dictionary<EquipSlot, string> replace)
    {
        _pathsSeen++;

        var m = EquipPattern.Match(gamePath);
        if (m.Success)
        {
            var slot = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "met" => EquipSlot.Head,
                "top" => EquipSlot.Body,
                "glv" => EquipSlot.Hands,
                "dwn" => EquipSlot.Legs,
                "sho" => EquipSlot.Feet,
                _     => EquipSlot.Unknown,
            };
            slots.Add(slot);
            if (slot != EquipSlot.Unknown && ushort.TryParse(m.Groups[1].Value, out var id))
                Record(setIds, slot, id, IsModelPath(gamePath));
            return;
        }

        m = AccessoryPattern.Match(gamePath);
        if (m.Success)
        {
            var slot = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ear" => EquipSlot.Ears,
                "nek" => EquipSlot.Neck,
                "wrs" => EquipSlot.Wrists,
                "rir" => EquipSlot.RingRight,
                "ril" => EquipSlot.RingLeft,
                _     => EquipSlot.Unknown,
            };
            slots.Add(slot);
            if (slot != EquipSlot.Unknown && ushort.TryParse(m.Groups[1].Value, out var id))
                Record(setIds, slot, id, IsModelPath(gamePath));
            return;
        }

        m = WeaponPattern.Match(gamePath);
        if (m.Success)
        {
            slots.Add(EquipSlot.MainHand);
            if (ushort.TryParse(m.Groups[1].Value, out var id))
                Record(setIds, EquipSlot.MainHand, id, IsModelPath(gamePath));
            return;
        }

        // Customisation replaces part of the character model, so there is no equipment set ID
        // and nothing to look up in the Item sheet — only the slot is recorded.
        m = CustomizationPattern.Match(gamePath);
        if (m.Success)
        {
            var slot = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "hair" => EquipSlot.Hair,
                "face" => EquipSlot.Face,
                "tail" => EquipSlot.Tail,
                "zear" => EquipSlot.VieraEars,
                "body" => EquipSlot.Skin,
                _      => EquipSlot.Unknown,
            };
            slots.Add(slot);

            if (slot == EquipSlot.Unknown) return;
            if (!ushort.TryParse(m.Groups[3].Value, out var customizeId)) return;

            // The model is what says which hairstyle (or face, or tail) is being replaced. A texture
            // under another id is a supporting file — see Detected — so it only answers when no model
            // does, which is what keeps a plain retexture working.
            var fromModel = IsModelPath(gamePath);

            // A model for this part means the mod reshapes it rather than repainting it, which is
            // what a customisation item's layer is decided from
            if (fromModel) _modelSlots.Add(slot);

            // Every id, not the first: a mod covering four faces across two races has to be able to
            // say so, or a design setting the third of them looks like a mismatch
            if (int.TryParse(m.Groups[1].Value, out var coverRace))
            {
                if (!_coverage.TryGetValue(slot, out var byRace))
                    _coverage[slot] = byRace = new Dictionary<int, SortedSet<ushort>>();
                if (!byRace.TryGetValue(coverRace, out var ids))
                    byRace[coverRace] = ids = new SortedSet<ushort>();
                ids.Add(customizeId);
            }

            // Kept as a fallback for when the player's race is unknown or the mod covers only one
            Record(setIds, slot, customizeId, fromModel);

            // Hairstyle numbers differ per race, so record which race each one belongs to
            if (slot == EquipSlot.Hair && int.TryParse(m.Groups[1].Value, out var raceCode))
                Record(hairIds, raceCode, customizeId, fromModel);
            return;
        }

        // The mod categories. These come after equipment and customisation deliberately: a gear mod
        // that also ships a glow or a custom animation is still a gear mod, and classifying it by
        // whichever path happened to be read first would be a coin toss.
        m = AnimationPattern.Match(gamePath);
        if (m.Success)
        {
            slots.Add(EquipSlot.Animation);
            replace.TryAdd(EquipSlot.Animation, m.Groups[1].Value.ToLowerInvariant());
            return;
        }

        m = MonsterPattern.Match(gamePath);
        if (m.Success)
        {
            slots.Add(EquipSlot.Mount);
            replace.TryAdd(EquipSlot.Mount,
                $"{m.Groups[1].Value.ToLowerInvariant()}{m.Groups[2].Value}");
            return;
        }

        // No replace key: VFX overlap in ways the file paths do not reveal, so each one is left
        // independent rather than guessing which two mods collide.
        if (VfxPattern.IsMatch(gamePath))
        {
            slots.Add(EquipSlot.Vfx);
            return;
        }

        if (CommonPattern.IsMatch(gamePath))
        {
            slots.Add(EquipSlot.Other);
            return;
        }

        // Counted rather than turned into a slot here: a gear mod that ships one custom texture is
        // still a gear mod, and only a mod made of nothing else lets these say what it is.
        if (CustomTexturePattern.IsMatch(gamePath))
            _customTextures++;
    }

    // ── JSON POCOs ────────────────────────────────────────────────────────────

    /// <summary>meta.json for FileVersion 4+, which carries the groups inline.</summary>
    private class MetaJson
    {
        [JsonPropertyName("FileVersion")]
        public int FileVersion { get; set; }

        [JsonPropertyName("Groups")]
        public List<GroupJson>? Groups { get; set; }

        /// <summary>
        /// The files the mod applies with no option chosen — what FileVersion 3 kept in
        /// default_mod.json. Shaped like an option, so it is read as one.
        /// </summary>
        [JsonPropertyName("DefaultData")]
        public OptionJson? DefaultData { get; set; }
    }

    private class GroupJson
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        // "Single" = radio/dropdown, "Multi" = checkboxes
        [JsonPropertyName("Type")]
        public string Type { get; set; } = "Single";

        [JsonPropertyName("Options")]
        public List<OptionJson> Options { get; set; } = new();
    }

    private class OptionJson
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Files")]
        public Dictionary<string, string>? Files { get; set; }

        [JsonPropertyName("FileSwaps")]
        public Dictionary<string, string>? FileSwaps { get; set; }
    }
}
