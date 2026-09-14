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
    /// "Size: YAB+ M" under the card's buttons, opening one list of every size option the item has.
    /// </summary>
    /// <remarks>
    /// Small, under the action row, and only when the card has the height — the same terms as the
    /// solo row it sits beside. The right-click menu has the same thing for a card that does not.
    /// <para>
    /// One button however many groups feed it. With one group it reads the option and opens
    /// straight to the list; with several it reads "Size Options" and opens to a menu per group,
    /// each named for the group and where it stands, each its own pick. Or, by the setting, as one
    /// list that is one set of sizes. Options hidden in Edit are left off unless they are the one
    /// the item is at.
    /// </para>
    /// </remarks>
    private void DrawCardSizeRow(WardrobeItem item)
    {
        if (!_config.SizeOptionsEnabled) return;

        var groups = SizeGroupsOf(item).ToList();
        if (groups.Count == 0) return;

        var needed = ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (ImGui.GetContentRegionAvail().Y < needed) return;

        var label = FitToWidth(ButtonLabel(groups), ImGui.GetContentRegionAvail().X - ImGui.GetStyle().FramePadding.X * 2);

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.62f, 0.62f, 0.70f, 1f));

        var popup = $"##sizepop_{item.Id}";
        if (ImGui.SmallButton($"{label}##size_{item.Id}"))
            ImGui.OpenPopup(popup);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", groups.Select(g => $"{g.Group}: {ShownFor(g.Mod, g.Group)}")) +
                             "\n\n" + "Click to change.");

        ImGui.PopStyleColor(3);

        if (!ImGui.BeginPopup(popup)) return;
        DrawSizeEntries(item, groups, asMenu: false);
        ImGui.EndPopup();
    }

    /// <summary>
    /// "Size: nymph" for one group. For several, "Size Options" when they open to a menu each, or
    /// what is in effect when they open to one list.
    /// </summary>
    private string ButtonLabel(List<(ModReference Mod, string Group)> groups) =>
        groups.Count == 1          ? $"Size: {ShownFor(groups[0].Mod, groups[0].Group)}"
        : _config.SizeOptionsOneList ? $"Size: {string.Join(" · ", InEffect(groups))}"
        : "Size Options";

    /// <summary>
    /// The options that are in effect: any switched-on toggles, else the dropdowns' choices.
    /// </summary>
    /// <remarks>
    /// A refit shipped as a mod of its own is a toggle over the body underneath: with Muse on the
    /// character is wearing Muse, and the base mod's "YAB mini" is what it will go back to, not what
    /// it is at. So while any toggle is on the button reads the toggles, and only once they are all
    /// off does it read the dropdowns. The tooltip and the list still show every group where it
    /// stands.
    /// </remarks>
    private static IEnumerable<string> InEffect(List<(ModReference Mod, string Group)> groups)
    {
        var toggles = groups
            .Where(g => !g.Mod.Options.ContainsKey(g.Group) && StoredOption(g.Mod, g.Group) != null)
            .Select(g => ShownFor(g.Mod, g.Group))
            .ToList();
        if (toggles.Count > 0) return toggles;
        return groups.Select(g => ShownFor(g.Mod, g.Group));
    }

    /// <summary>What the card shows for one group: its stored option by its card name, or "as is".</summary>
    private static string ShownFor(ModReference mod, string group) =>
        StoredOption(mod, group) is { } option ? mod.SizeOptionLabel(group, option) : "as is";

    /// <summary>
    /// The popup's contents: one group's options directly; several as — as the View menu's Crop
    /// Guide does — a submenu per group, each named for the group and the option it is at, with
    /// the option in force ticked inside; or, by the setting, several as one list with a rule
    /// between groups. As menus the groups are independent; as one list they are one set of sizes,
    /// and a pick in one switches the toggles in the others off.
    /// </summary>
    private void DrawSizeEntries(WardrobeItem item, List<(ModReference Mod, string Group)> groups, bool asMenu)
    {
        if (groups.Count == 1)
        {
            var (mod, group) = groups[0];
            if (!asMenu)
            {
                ImGui.TextDisabled(group);
                ImGui.Separator();
            }
            DrawGroupEntries(item, groups, mod, group, asMenu);
            return;
        }

        if (_config.SizeOptionsOneList)
        {
            var first = true;
            foreach (var (mod, group) in groups)
            {
                if (!first) ImGui.Separator();
                first = false;
                ImGui.PushID($"{mod.ModDirectory}|{group}");
                DrawGroupEntries(item, groups, mod, group, asMenu);
                ImGui.PopID();
            }
            return;
        }

        foreach (var (mod, group) in groups)
        {
            if (!ImGui.BeginMenu($"{group}: {ShownFor(mod, group)}##{mod.ModDirectory}_{group}")) continue;
            DrawGroupEntries(item, groups, mod, group, asMenu: true);
            ImGui.EndMenu();
        }
    }

    /// <summary>One group's options, the ones in force ticked, hidden ones left off.</summary>
    private void DrawGroupEntries(WardrobeItem item, List<(ModReference Mod, string Group)> groups,
        ModReference mod, string group, bool asMenu)
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
            if (!on && mod.IsSizeOptionHidden(group, option)) continue;

            var name   = mod.SizeOptionLabel(group, option);
            var picked = asMenu
                ? ImGui.MenuItem($"{name}##{option}", string.Empty, on)
                : ImGui.Selectable($"{name}##{option}", on);
            if (!picked) continue;

            if (single) { if (option != current) PickSingle(item, groups, mod, def, option); }
            else        PickCheckbox(item, groups, mod, def, option, !on);
        }
    }

    /// <summary>
    /// Sets a dropdown to the pick. As one list, the item's other size groups' toggles go off with
    /// it; as a menu each, they are not touched.
    /// </summary>
    private void PickSingle(WardrobeItem item, List<(ModReference Mod, string Group)> groups,
        ModReference mod, ModOptionGroup def, string chosen)
    {
        mod.Options[def.GroupName] = chosen;
        var changed = new List<(ModReference, string)> { (mod, def.GroupName) };
        if (_config.SizeOptionsOneList) changed.AddRange(SwitchOffOtherToggles(groups, mod, def.GroupName));
        Commit(item, changed);
    }

    /// <summary>
    /// Ticks or unticks one option of a checkbox group. Ticking a size unticks the other sizes in
    /// the same group — and, as one list, the toggles in the item's other size groups.
    /// </summary>
    private void PickCheckbox(WardrobeItem item, List<(ModReference Mod, string Group)> groups,
        ModReference mod, ModOptionGroup def, string option, bool on)
    {
        // Every option this pick decides: the one clicked, and the other sizes when a size goes on.
        // Nothing else in the group is written, so an extra beside the sizes stays however it was
        var decided = new Dictionary<string, bool> { [option] = on };
        if (on && SizeGuess.ReadsAsSize(option))
            foreach (var other in def.OptionNames)
                if (other != option && SizeGuess.ReadsAsSize(other)) decided[other] = false;

        WriteStates(mod, def.GroupName, decided);
        var changed = new List<(ModReference, string)> { (mod, def.GroupName) };
        if (on && _config.SizeOptionsOneList) changed.AddRange(SwitchOffOtherToggles(groups, mod, def.GroupName));
        Commit(item, changed);
    }

    /// <summary>
    /// Turns off what is on in the item's other checkbox size groups, and says which changed.
    /// </summary>
    /// <remarks>
    /// The one-list half of the setting: one list is one set of sizes, so a body at "nymph" is
    /// not also wearing Muse, and Muse switched on sits over whatever the dropdown says. A group
    /// with one option is a toggle — a refit's on/off — and the whole of it goes off. A group with
    /// several is a set of sizes with, perhaps, an extra or two beside them, and only the options
    /// that read as sizes go off; the extra stays. Dropdowns are left alone, since a dropdown
    /// cannot be off. As a menu per group this never runs: each group is its own pick there.
    /// </remarks>
    private List<(ModReference, string)> SwitchOffOtherToggles(
        List<(ModReference Mod, string Group)> groups, ModReference pickedMod, string pickedGroup)
    {
        var changed = new List<(ModReference, string)>();
        foreach (var (mod, group) in groups)
        {
            if (ReferenceEquals(mod, pickedMod) && group == pickedGroup) continue;
            var def = SizeGroup(mod, group);
            if (def == null || def.GroupType == ModGroupType.Single) continue;

            var off = def.OptionNames
                .Where(o => IsOn(mod, group, o) && (def.OptionNames.Count == 1 || SizeGuess.ReadsAsSize(o)))
                .ToDictionary(o => o, _ => false);
            if (off.Count == 0) continue;

            WriteStates(mod, group, off);
            changed.Add((mod, group));
        }
        return changed;
    }

    /// <summary>Writes decided on/off states into whichever field the mod's checkbox options live in.</summary>
    private static void WriteStates(ModReference mod, string group, Dictionary<string, bool> decided)
    {
        if (mod.OptionStates.Count == 0 && mod.MultiOptions.Count > 0)
        {
            // Still on the whole-selection field: keep it there, or the wear path would switch
            // over to tri-states and drop the rest of the selection
            var selection = mod.MultiOptions.TryGetValue(group, out var had)
                ? new List<string>(had) : new List<string>();
            foreach (var (name, value) in decided)
            {
                selection.Remove(name);
                if (value) selection.Add(name);
            }
            mod.MultiOptions[group] = selection;
        }
        else
        {
            if (!mod.OptionStates.TryGetValue(group, out var states))
                mod.OptionStates[group] = states = new Dictionary<string, bool>();
            foreach (var (name, value) in decided) states[name] = value;
        }
    }

    /// <summary>Saves, and sends every changed group if the item is on.</summary>
    private void Commit(WardrobeItem item, List<(ModReference Mod, string Group)> changed)
    {
        _config.Save();
        if (!_wardrobe.IsItemWorn(item)) return;
        foreach (var (mod, group) in changed) _wardrobe.ReapplyGroup(item, mod, group);
    }

    /// <summary>The text, or as much of it as fits in the width with an ellipsis.</summary>
    private static string FitToWidth(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        var ellipsis = ImGui.CalcTextSize("…").X;
        var keep = text.Length;
        while (keep > 0 && ImGui.CalcTextSize(text[..keep]).X + ellipsis > width) keep--;
        return keep == 0 ? "…" : text[..keep].TrimEnd() + "…";
    }

    /// <summary>The same pick as a "Size" submenu on the card's right-click menu.</summary>
    private void DrawMenuSizes(WardrobeItem item)
    {
        if (!_config.SizeOptionsEnabled) return;

        var groups = SizeGroupsOf(item).ToList();
        if (groups.Count == 0) return;

        if (!ImGui.BeginMenu(ButtonLabel(groups))) return;
        DrawSizeEntries(item, groups, asMenu: true);
        ImGui.EndMenu();
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
        var oneList = _config.SizeOptionsOneList;
        if (ImGui.Checkbox("Show an item's size groups as one list", ref oneList))
        {
            _config.SizeOptionsOneList = oneList;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("An item with several size groups — a body's sizes and a refit's" + "\n" +
                             "toggle — opens to one list of all of them, and they act as one set:" + "\n" +
                             "pick a body size and the refit's toggle goes off, pick the toggle" + "\n" +
                             "and it goes on over the body size. The button reads what is in" + "\n" +
                             "effect. Off, it opens to a menu per group, and each group is its" + "\n" +
                             "own pick — nothing in one changes anything in another." + "\n\n" +
                             "Items with one size group open to its list whichever this is.");

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
