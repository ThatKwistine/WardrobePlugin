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
    IReadOnlyDictionary<int, ushort> HairIdsByRace
);

public class ModAnalysisService
{
    private readonly IPluginLog? _log;

    public ModAnalysisService(IPluginLog? log = null) => _log = log;

    // chara/equipment/e{SetId}/model/c{race}e{SetId}_{slot}.mdl  — group 1 = SetId, group 2 = slot suffix
    private static readonly Regex EquipPattern =
        new(@"chara/equipment/e(\d+)/model/c\d+e\d+_(met|top|glv|dwn|sho)\.mdl",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // chara/accessory/a{SetId}/model/c{race}a{SetId}_{slot}.mdl  — group 1 = SetId, group 2 = slot suffix
    private static readonly Regex AccessoryPattern =
        new(@"chara/accessory/a(\d+)/model/c\d+a\d+_(ear|nek|wrs|rir|ril)\.mdl",
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
        var slots   = new HashSet<EquipSlot>();
        var setIds  = new Dictionary<EquipSlot, ushort>();
        var hairIds = new Dictionary<int, ushort>();
        var groups  = new List<ModOptionGroup>();

        if (!Directory.Exists(modFolderPath))
            return new ModAnalysisResult(slots, groups, setIds, hairIds);

        // default_mod.json
        var defaultFile = Path.Combine(modFolderPath, "default_mod.json");
        if (File.Exists(defaultFile))
            foreach (var key in ReadFileKeys(defaultFile))
                ClassifyPath(key, slots, setIds, hairIds);

        // group_NNN_*.json
        foreach (var groupFile in Directory.GetFiles(modFolderPath, "group_*.json").OrderBy(x => x))
        {
            var g = TryReadGroup(groupFile);
            if (g is null) continue;

            var optNames = new List<string>();
            foreach (var opt in g.Options)
            {
                optNames.Add(opt.Name);
                if (opt.Files != null)
                    foreach (var key in opt.Files.Keys)
                        ClassifyPath(key, slots, setIds, hairIds);
            }

            if (optNames.Count > 0)
                groups.Add(new ModOptionGroup(g.Name, ClassifyGroupType(g.Type, g.Name), optNames));
        }

        slots.Remove(EquipSlot.Unknown);

        // chara/common files are usually supporting assets for a real equipment mod — a shared ID
        // texture shipped alongside an accessory model, for instance. Only treat them as a slot in
        // their own right when nothing else was found, or every such mod gains a phantom Other slot.
        if (slots.Count > 1) slots.Remove(EquipSlot.Other);

        return new ModAnalysisResult(slots, groups, setIds, hairIds);
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

    private static void ClassifyPath(string gamePath, HashSet<EquipSlot> slots, Dictionary<EquipSlot, ushort> setIds, Dictionary<int, ushort> hairIds)
    {
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

        if (CommonPattern.IsMatch(gamePath))
            slots.Add(EquipSlot.Other);
    }

    // ── JSON POCOs ────────────────────────────────────────────────────────────

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
