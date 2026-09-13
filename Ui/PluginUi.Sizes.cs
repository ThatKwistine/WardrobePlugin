using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Models;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// The size pick on a card: a mod's own size options, right there.
/// </summary>
/// <remarks>
/// Issue #28's mock-up, taken as drawn. A group of the item's mod is marked as its size — on
/// import, usually — and the card shows that group's options by the mod's own names. Picking one
/// sets the item's stored option and, if the item is on, sends it. There is no translation into a
/// size name of the plugin's own: mods do not agree on any, and the person choosing between
/// "YAB+ M" and "Bibo+" knows what they are looking at.
/// </remarks>
public partial class PluginUi
{
    /// <summary>The item's size groups: which mod, which group.</summary>
    private static IEnumerable<(ModReference Mod, string Group)> SizeGroupsOf(WardrobeItem item) =>
        item.Mods.SelectMany(m => m.SizeGroups.Select(g => (m, g)));

    /// <summary>The option the item stores for the group, or null for a group it leaves alone.</summary>
    /// <remarks>
    /// For a checkbox group, the ticked option that reads as a size before any other ticked one, so
    /// a group with "Large" and "Nipple fix" both on is labelled Large.
    /// </remarks>
    private static string? StoredOption(ModReference mod, string group)
    {
        if (mod.Options.TryGetValue(group, out var single)) return single;

        IEnumerable<string> on;
        if (mod.OptionStates.TryGetValue(group, out var states))
            on = states.Where(kv => kv.Value).Select(kv => kv.Key);
        else if (mod.MultiOptions.TryGetValue(group, out var list))
            on = list;
        else
            return null;

        var ticked = on.ToList();
        return ticked.FirstOrDefault(SizeGuess.ReadsAsSize) ?? ticked.FirstOrDefault();
    }

    /// <summary>Whether an option in a checkbox group is on, as the item stores it.</summary>
    private static bool IsOn(ModReference mod, string group, string option) =>
        mod.OptionStates.TryGetValue(group, out var states)
            ? states.TryGetValue(option, out var on) && on
            : mod.MultiOptions.TryGetValue(group, out var list) && list.Contains(option);

    /// <summary>The group as the mod defines it, read once and kept.</summary>
    /// <remarks>
    /// Reading a mod's groups means walking its folder, which is not a thing to do on every frame a
    /// popup is open. Kept for the session: a group's options change when the mod is updated, which
    /// is rare, and the edit panel's Reload Options covers the same ground when it happens.
    /// </remarks>
    private readonly Dictionary<string, ModOptionGroup?> _sizeGroupCache = new();

    private ModOptionGroup? SizeGroup(ModReference mod, string group)
    {
        var key = $"{mod.ModDirectory}|{group}";
        if (_sizeGroupCache.TryGetValue(key, out var had)) return had;

        ModOptionGroup? found = null;
        var path = Plugin.Penumbra.GetModFolderPath(mod.ModDirectory);
        if (path != null && System.IO.Directory.Exists(path))
            found = _analysis.Analyze(path).OptionGroups.FirstOrDefault(g => g.GroupName == group);
        return _sizeGroupCache[key] = found;
    }

    /// <summary>
    /// "Size: YAB+ M" under the card's buttons, opening the group's options.
    /// </summary>
    /// <remarks>
    /// Small, under the action row, and only when the card has the height — the same terms as the
    /// solo row it sits beside. The right-click menu has the same thing for a card that does not.
    /// The group's name is used instead of "Size" when the item has more than one, so a bust and a
    /// hips group can be told apart.
    /// </remarks>
    private void DrawCardSizeRow(WardrobeItem item)
    {
        if (!_config.SizeOptionsEnabled) return;

        var groups = SizeGroupsOf(item).ToList();
        if (groups.Count == 0) return;

        var needed = ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (ImGui.GetContentRegionAvail().Y < needed) return;

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.62f, 0.62f, 0.70f, 1f));

        var first = true;
        foreach (var (mod, group) in groups)
        {
            var label = $"{(groups.Count == 1 ? "Size" : group)}: {StoredOption(mod, group) ?? "as is"}";
            if (!first) UiLayout.SameLineIfRoom(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2);
            first = false;

            var popup = $"##sizepop_{mod.ModDirectory}_{group}";
            if (ImGui.SmallButton($"{label}##size_{mod.ModDirectory}_{group}"))
                ImGui.OpenPopup(popup);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{group} — this item's size option. Click to change it.");

            if (ImGui.BeginPopup(popup))
            {
                ImGui.TextDisabled(group);
                ImGui.Separator();
                DrawSizeOptionEntries(item, mod, group, asMenu: false);
                ImGui.EndPopup();
            }
        }

        ImGui.PopStyleColor(3);
    }

    /// <summary>The group's options, the current one ticked.</summary>
    /// <remarks>
    /// A dropdown group is a pick of one. A checkbox group shows every option ticked as the item
    /// has it: picking an option that reads as a size turns the other size options off with it,
    /// since they are alternatives, and picking one that does not — a fix, an extra — just toggles
    /// it, because that is what a checkbox is.
    /// </remarks>
    private void DrawSizeOptionEntries(WardrobeItem item, ModReference mod, string group, bool asMenu)
    {
        var def = SizeGroup(mod, group);
        if (def == null || def.OptionNames.Count == 0)
        {
            ImGui.TextDisabled("The mod's options could not be read.");
            return;
        }

        var single  = def.GroupType == ModGroupType.Single;
        var current = StoredOption(mod, group);
        foreach (var option in def.OptionNames)
        {
            var on = single ? option == current : IsOn(mod, group, option);
            var picked = asMenu
                ? ImGui.MenuItem(option, string.Empty, on)
                : ImGui.Selectable(option, on);
            if (!picked) continue;
            if (single) { if (option != current) PickSingle(item, mod, def, option); }
            else        PickCheckbox(item, mod, def, option, !on);
        }
    }

    /// <summary>Writes a dropdown pick into the item's own options, and sends it if the item is on.</summary>
    private void PickSingle(WardrobeItem item, ModReference mod, ModOptionGroup def, string chosen)
    {
        mod.Options[def.GroupName] = chosen;
        _config.Save();
        if (_wardrobe.IsItemWorn(item)) _wardrobe.ReapplyGroup(item, mod, def.GroupName);
    }

    /// <summary>
    /// Ticks or unticks one option of a checkbox group. Ticking a size unticks the other sizes.
    /// </summary>
    private void PickCheckbox(WardrobeItem item, ModReference mod, ModOptionGroup def, string option, bool on)
    {
        // Every option this pick decides: the one clicked, and the other sizes when a size goes on.
        // Nothing else in the group is written, so an extra beside the sizes stays however it was
        var decided = new Dictionary<string, bool> { [option] = on };
        if (on && SizeGuess.ReadsAsSize(option))
            foreach (var other in def.OptionNames)
                if (other != option && SizeGuess.ReadsAsSize(other)) decided[other] = false;

        if (mod.OptionStates.Count == 0 && mod.MultiOptions.Count > 0)
        {
            // Still on the whole-selection field: keep it there, or the wear path would switch
            // over to tri-states and drop the rest of the selection
            var selection = mod.MultiOptions.TryGetValue(def.GroupName, out var had)
                ? new List<string>(had) : new List<string>();
            foreach (var (name, value) in decided)
            {
                selection.Remove(name);
                if (value) selection.Add(name);
            }
            mod.MultiOptions[def.GroupName] = selection;
        }
        else
        {
            if (!mod.OptionStates.TryGetValue(def.GroupName, out var states))
                mod.OptionStates[def.GroupName] = states = new Dictionary<string, bool>();
            foreach (var (name, value) in decided) states[name] = value;
        }

        _config.Save();
        if (_wardrobe.IsItemWorn(item)) _wardrobe.ReapplyGroup(item, mod, def.GroupName);
    }

    /// <summary>The same pick as a submenu on the card's right-click menu.</summary>
    private void DrawMenuSizes(WardrobeItem item)
    {
        if (!_config.SizeOptionsEnabled) return;

        var groups = SizeGroupsOf(item).ToList();
        foreach (var (mod, group) in groups)
        {
            var label = $"{(groups.Count == 1 ? "Size" : group)}: {StoredOption(mod, group) ?? "as is"}";
            if (!ImGui.BeginMenu(label)) continue;
            DrawSizeOptionEntries(item, mod, group, asMenu: true);
            ImGui.EndMenu();
        }
    }

    /// <summary>Settings → Penumbra & Mods → Size Options: the whole feature, and the recognition.</summary>
    private void DrawSizeSettings()
    {
        Hint("A body mod's size group, on the item's card.",
             "A group of the mod that is a size — a bust, a thigh — goes on the card as" + "\n" +
             "a small pick of its own options, so a top can be changed between bodies" + "\n" +
             "without opening Edit. Which group is the size is found on import for body" + "\n" +
             "and legs items, and is a tick under any group in Edit.");
        ImGui.Spacing();

        var enabled = _config.SizeOptionsEnabled;
        if (ImGui.Checkbox("Size options on cards", ref enabled))
        {
            _config.SizeOptionsEnabled = enabled;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off hides the pick on every card and its entry on the right-click" + "\n" +
                             "menu, the tick in Edit, and stops imports marking groups. Nothing" + "\n" +
                             "is forgotten: turn it back on and every group is as it was marked.");

        if (!enabled) return;

        ImGui.Indent();
        var guess = _config.GuessSizeGroups;
        if (ImGui.Checkbox("Recognise size groups when importing", ref guess))
        {
            _config.GuessSizeGroups = guess;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A body or legs item whose mod has a group with options that plainly" + "\n" +
                             "read as sizes — Small / Medium / Large, S / M / L — gets that group" + "\n" +
                             "marked as its size on import and on Re-detect, so the pick is on" + "\n" +
                             "the card. Never changes a group you have marked or unmarked yourself." + "\n\n" +
                             "Dropdowns and checkbox groups alike.");
        ImGui.Unindent();
    }
}
