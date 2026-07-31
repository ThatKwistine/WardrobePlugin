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
    IReadOnlyDictionary<EquipSlot, ushort> SlotSetIds
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
    private static readonly Regex CustomizationPattern =
        new(@"chara/human/c\d+/obj/(hair|face|tail|zear|body)/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        var groups  = new List<ModOptionGroup>();

        if (!Directory.Exists(modFolderPath))
            return new ModAnalysisResult(slots, groups, setIds);

        // default_mod.json
        var defaultFile = Path.Combine(modFolderPath, "default_mod.json");
        if (File.Exists(defaultFile))
            foreach (var key in ReadFileKeys(defaultFile))
                ClassifyPath(key, slots, setIds);

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
                        ClassifyPath(key, slots, setIds);
            }

            if (optNames.Count > 0)
                groups.Add(new ModOptionGroup(g.Name, ClassifyGroupType(g.Type, g.Name), optNames));
        }

        slots.Remove(EquipSlot.Unknown);
        return new ModAnalysisResult(slots, groups, setIds);
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

    private static void ClassifyPath(string gamePath, HashSet<EquipSlot> slots, Dictionary<EquipSlot, ushort> setIds)
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
            var slot = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "hair" => EquipSlot.Hair,
                "face" => EquipSlot.Face,
                "tail" => EquipSlot.Tail,
                "zear" => EquipSlot.VieraEars,
                "body" => EquipSlot.Skin,
                _      => EquipSlot.Unknown,
            };
            slots.Add(slot);
        }
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
