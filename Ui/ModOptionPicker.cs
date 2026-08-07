using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// One of a mod's option groups: a dropdown for <see cref="ModGroupType.Single"/>, checkboxes for
/// everything else.
/// </summary>
/// <remarks>
/// Shared by the import panel, the edit panel's Mod Options section and Mass Import, which drew
/// their own copies of the same thing. Penumbra's <c>Imc</c> and <c>Combining</c> types are folded
/// into <see cref="ModGroupType.Multi"/> by <see cref="ModAnalysisService"/>, so only Single is ever
/// a one-of-N choice here.
/// </remarks>
public static class ModOptionPicker
{
    public static void Draw(ModOptionGroup group,
        Dictionary<string, int> singleSel, Dictionary<string, HashSet<string>> multiSel)
    {
        if (group.OptionNames.Count == 0) return;

        if (group.GroupType == ModGroupType.Single) DrawSingle(group, singleSel);
        else                                        DrawMulti(group, multiSel);
    }

    private static void DrawSingle(ModOptionGroup group, Dictionary<string, int> singleSel)
    {
        ImGui.TextDisabled(group.GroupName);

        if (!singleSel.ContainsKey(group.GroupName)) singleSel[group.GroupName] = 0;

        var idx   = singleSel[group.GroupName];
        var names = group.OptionNames;
        var label = idx >= 0 && idx < names.Count ? names[idx] : names[0];

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##{group.GroupName}", label)) return;

        for (var i = 0; i < names.Count; i++)
        {
            if (ImGui.Selectable($"{names[i]}##{group.GroupName}_{i}", i == idx))
                singleSel[group.GroupName] = i;
            if (i == idx) ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    /// <remarks>
    /// All and None sit beside the group's name rather than under it: a mod with several toggle
    /// groups would otherwise gain a row of buttons per group, which is more clutter than the
    /// checkboxes they act on. The count beside them says what the buttons would do without having
    /// to read down the list — a group showing 8/8 needs None, not All.
    /// </remarks>
    private static void DrawMulti(ModOptionGroup group, Dictionary<string, HashSet<string>> multiSel)
    {
        if (!multiSel.TryGetValue(group.GroupName, out var selected))
            multiSel[group.GroupName] = selected = new HashSet<string>();

        ImGui.TextDisabled(group.GroupName);

        // Nothing to enable or disable in bulk when there is a single checkbox
        if (group.OptionNames.Count > 1)
        {
            ImGui.PushID(group.GroupName);

            UiLayout.SameLineIfRoomForButton(" All ");
            if (ImGui.SmallButton(" All "))
                foreach (var opt in group.OptionNames)
                    selected.Add(opt);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tick every option in this group.");

            UiLayout.SameLineIfRoomForButton(" None ");
            if (ImGui.SmallButton(" None ")) selected.Clear();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Untick every option in this group.");

            var count = $"{selected.Count}/{group.OptionNames.Count}";
            UiLayout.SameLineIfRoomForText(count);
            ImGui.TextDisabled(count);

            ImGui.PopID();
        }

        foreach (var opt in group.OptionNames)
        {
            var isChecked = selected.Contains(opt);
            if (!ImGui.Checkbox($"{opt}##{group.GroupName}_{opt}", ref isChecked)) continue;

            if (isChecked) selected.Add(opt);
            else           selected.Remove(opt);
        }
    }
}
