using System.Collections.Generic;
using System.Numerics;
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
    /// <param name="multiOff">
    /// Options this item forces off, when it is allowed a third state. Null keeps the old
    /// two-state checkboxes, where anything unticked is turned off — which is still what the import
    /// panels want, since a fresh import is describing a whole look rather than adjusting one.
    /// </param>
    public static void Draw(ModOptionGroup group,
        Dictionary<string, int> singleSel, Dictionary<string, HashSet<string>> multiSel,
        Dictionary<string, HashSet<string>>? multiOff = null)
    {
        if (group.OptionNames.Count == 0) return;

        if (group.GroupType == ModGroupType.Single)      DrawSingle(group, singleSel);
        else if (multiOff == null)                       DrawMulti(group, multiSel);
        else                                             DrawTriState(group, multiSel, multiOff);
    }

    /// <summary>Selection index meaning "leave this group however it is".</summary>
    /// <remarks>
    /// A dropdown holds exactly one option, so it cannot have the checkbox groups' third state per
    /// option — "not this one" names no replacement for Penumbra to set. The group as a whole can
    /// still be left alone though, which is the same idea one level up, and is what an item in
    /// another slot wants for a dropdown that has nothing to do with it.
    /// </remarks>
    public const int Ignore = -1;

    private static void DrawSingle(ModOptionGroup group, Dictionary<string, int> singleSel)
    {
        ImGui.TextDisabled(group.GroupName);

        if (!singleSel.ContainsKey(group.GroupName)) singleSel[group.GroupName] = 0;

        var idx   = singleSel[group.GroupName];
        var names = group.OptionNames;
        var label = idx >= 0 && idx < names.Count ? names[idx] : IgnoreLabel;

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##{group.GroupName}", label)) return;

        // First, so it is where the eye lands on a group being left out on purpose
        if (ImGui.Selectable($"{IgnoreLabel}##{group.GroupName}_ignore", idx < 0))
            singleSel[group.GroupName] = Ignore;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Do not set this group at all — whatever it is when the item is\n" +
                             "worn is what it stays. Use it for groups belonging to another slot.");

        ImGui.Separator();

        for (var i = 0; i < names.Count; i++)
        {
            if (ImGui.Selectable($"{names[i]}##{group.GroupName}_{i}", i == idx))
                singleSel[group.GroupName] = i;
            if (i == idx) ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private const string IgnoreLabel = "• Leave alone";

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

    /// <summary>
    /// Three states per option: force it on, force it off, or leave it however it is found.
    /// </summary>
    /// <remarks>
    /// Penumbra can only be told a group's whole selection, so the middle state is built by reading
    /// what is selected at the moment of wearing and putting the ignored options back unchanged.
    /// That is what lets two items from the same mod both be worn without one undoing the other —
    /// see <see cref="Ipc.PenumbraIpc.ApplyModOptionStates"/>.
    /// <para>
    /// Three explicit buttons rather than one that cycles: a cycling control has to be clicked an
    /// unpredictable number of times to reach a known state, and the whole point of the middle one
    /// is that it is a deliberate choice rather than a default nobody noticed.
    /// </para>
    /// </remarks>
    private static void DrawTriState(ModOptionGroup group,
        Dictionary<string, HashSet<string>> multiSel, Dictionary<string, HashSet<string>> multiOff)
    {
        if (!multiSel.TryGetValue(group.GroupName, out var on))
            multiSel[group.GroupName] = on = new HashSet<string>();
        if (!multiOff.TryGetValue(group.GroupName, out var off))
            multiOff[group.GroupName] = off = new HashSet<string>();

        ImGui.TextDisabled(group.GroupName);

        if (group.OptionNames.Count > 1)
        {
            ImGui.PushID($"bulk_{group.GroupName}");

            UiLayout.SameLineIfRoomForButton(" All on ");
            if (ImGui.SmallButton(" All on "))
            {
                foreach (var opt in group.OptionNames) on.Add(opt);
                off.Clear();
            }

            UiLayout.SameLineIfRoomForButton(" Ignore all ");
            if (ImGui.SmallButton(" Ignore all "))
            {
                on.Clear();
                off.Clear();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Leave this whole group as whatever it happens to be.\n" +
                                 "Use it for groups that belong to another slot's item.");

            var ignored = group.OptionNames.Count - on.Count - off.Count;
            var summary = $"{on.Count} on · {ignored} ignored · {off.Count} off";
            UiLayout.SameLineIfRoomForText(summary);
            ImGui.TextDisabled(summary);

            ImGui.PopID();
        }

        foreach (var opt in group.OptionNames)
        {
            ImGui.PushID($"{group.GroupName}_{opt}");

            var state = on.Contains(opt) ? 1 : off.Contains(opt) ? -1 : 0;

            if (StateButton("✓", state == 1, OnColour, "Turn this option on."))
            {
                on.Add(opt);
                off.Remove(opt);
            }

            ImGui.SameLine();
            if (StateButton("•", state == 0, IgnoreColour, "Leave this option however it already is."))
            {
                on.Remove(opt);
                off.Remove(opt);
            }

            ImGui.SameLine();
            if (StateButton("✕", state == -1, OffColour, "Turn this option off."))
            {
                off.Add(opt);
                on.Remove(opt);
            }

            ImGui.SameLine();
            if (state == 0) ImGui.TextDisabled(opt);
            else            ImGui.TextUnformatted(opt);

            ImGui.PopID();
        }
    }

    private static readonly Vector4 OnColour     = new(0.16f, 0.42f, 0.18f, 1f);
    private static readonly Vector4 IgnoreColour = new(0.32f, 0.32f, 0.38f, 1f);
    private static readonly Vector4 OffColour    = new(0.45f, 0.12f, 0.12f, 1f);

    /// <summary>One of the three state buttons, filled while it is the option's current state.</summary>
    private static bool StateButton(string glyph, bool active, Vector4 colour, string tooltip)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, active ? colour : new Vector4(0.16f, 0.16f, 0.19f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colour);
        ImGui.PushStyleColor(ImGuiCol.Text,
            active ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.5f, 0.5f, 0.56f, 1f));

        var clicked = ImGui.SmallButton(glyph);

        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }
}
