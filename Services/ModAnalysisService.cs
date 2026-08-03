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

public record ModOptionGroup(string GroupName, ModGroupType GroupType, IReadOnlyList<string> OptionNames);

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
    /// What the mod replaces within a mod category, keyed by slot — the animation file name for an
    /// emote, the monster id for a mount. Equipment identifies itself by set ID instead, so only
    /// mod categories ever appear here. See <see cref="Models.WardrobeItem.Replaces"/>.
    /// </summary>
    IReadOnlyDictionary<EquipSlot, string> ReplaceKeys
);

public class ModAnalysisService
{
    private readonly IPluginLog? _log;

    /// <summary>Game paths seen during the current Analyze call, used to spot a layout we cannot read.</summary>
    private int _pathsSeen;

    public ModAnalysisService(IPluginLog? log = null) => _log = log;

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
    /// Reads the Penumbra mod folder and returns which equipment slots it touches
    /// plus any option groups and their option names.
    /// </summary>
    public ModAnalysisResult Analyze(string modFolderPath)
    {
        _pathsSeen  = 0;
        var slots   = new HashSet<EquipSlot>();
        var setIds  = new Dictionary<EquipSlot, ushort>();
        var hairIds = new Dictionary<int, ushort>();
        var replace = new Dictionary<EquipSlot, string>();
        var groups  = new List<ModOptionGroup>();

        if (!Directory.Exists(modFolderPath))
            return new ModAnalysisResult(slots, groups, setIds, hairIds, replace);

        // default_mod.json
        var defaultFile = Path.Combine(modFolderPath, "default_mod.json");
        if (File.Exists(defaultFile))
            foreach (var key in ReadFileKeys(defaultFile))
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
        var meta = ReadMeta(Path.Combine(modFolderPath, "meta.json"));
        foreach (var g in meta?.Groups ?? new List<GroupJson>())
            AddGroup(g, slots, setIds, hairIds, replace, groups);

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

        return new ModAnalysisResult(slots, groups, setIds, hairIds, replace);
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
            if (doc.RootElement.TryGetProperty("Files", out var files) &&
                files.ValueKind == JsonValueKind.Object)
            {
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
    private void AddGroup(GroupJson g, HashSet<EquipSlot> slots, Dictionary<EquipSlot, ushort> setIds,
        Dictionary<int, ushort> hairIds, Dictionary<EquipSlot, string> replace, List<ModOptionGroup> groups)
    {
        var optNames = new List<string>();
        foreach (var opt in g.Options)
        {
            optNames.Add(opt.Name);
            if (opt.Files == null) continue;

            foreach (var key in opt.Files.Keys)
                ClassifyPath(key, slots, setIds, hairIds, replace);
        }

        if (optNames.Count > 0)
            groups.Add(new ModOptionGroup(g.Name, ClassifyGroupType(g.Type, g.Name), optNames));
    }

    private void ClassifyPath(string gamePath, HashSet<EquipSlot> slots, Dictionary<EquipSlot, ushort> setIds,
        Dictionary<int, ushort> hairIds, Dictionary<EquipSlot, string> replace)
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
                setIds.TryAdd(slot, id);
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
                setIds.TryAdd(slot, id);
            return;
        }

        m = WeaponPattern.Match(gamePath);
        if (m.Success)
        {
            slots.Add(EquipSlot.MainHand);
            if (ushort.TryParse(m.Groups[1].Value, out var id))
                setIds.TryAdd(EquipSlot.MainHand, id);
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

            // Kept as a fallback for when the player's race is unknown or the mod covers only one
            setIds.TryAdd(slot, customizeId);

            // Hairstyle numbers differ per race, so record which race each one belongs to
            if (slot == EquipSlot.Hair && int.TryParse(m.Groups[1].Value, out var raceCode))
                hairIds.TryAdd(raceCode, customizeId);
            return;
        }

        // The mod categories. These come after equipment and customisation deliberately: a gear mod
        // that also ships a glow or a custom animation is still a gear mod, and classifying it by
        // whichever path happened to be read first would be a coin toss.
        m = AnimationPattern.Match(gamePath);
        if (m.Success)
        {
            slots.Add(EquipSlot.Emote);
            replace.TryAdd(EquipSlot.Emote, m.Groups[1].Value.ToLowerInvariant());
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
            slots.Add(EquipSlot.Other);
    }

    // ── JSON POCOs ────────────────────────────────────────────────────────────

    /// <summary>meta.json for FileVersion 4+, which carries the groups inline.</summary>
    private class MetaJson
    {
        [JsonPropertyName("FileVersion")]
        public int FileVersion { get; set; }

        [JsonPropertyName("Groups")]
        public List<GroupJson>? Groups { get; set; }
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
    }
}
