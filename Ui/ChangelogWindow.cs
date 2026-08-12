using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WardrobePlugin.Services;

namespace WardrobePlugin.Ui;

/// <summary>
/// What changed, shown once after an update.
/// </summary>
/// <remarks>
/// The release page is written for someone deciding whether to install and reached by someone who
/// went looking for it. Most people update from the plugin installer and never see it, so anything
/// that needs doing after an update — re-saving a preset, setting a group of options again — was
/// only ever said where they were not looking. This says it where they are.
/// <para>
/// Its own window rather than a panel in the main one: it appears without being asked for, and
/// putting it inside the wardrobe would mean opening the wardrobe over whatever the user was doing.
/// </para>
/// </remarks>
public class ChangelogWindow : Window
{
    private readonly Configuration _config;

    /// <summary>Entries this opening shows: the new ones after an update, or all of them when asked.</summary>
    private IReadOnlyList<ChangelogEntry> _showing = Array.Empty<ChangelogEntry>();

    public ChangelogWindow(Configuration config)
        : base("What's new in Wardrobe###WardrobeChangelog")
    {
        _config = config;

        Size          = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary>
    /// Opens on the versions newer than the one last seen, and marks this one seen.
    /// </summary>
    /// <remarks>
    /// The version is recorded as soon as it is shown rather than when the window is closed, so this
    /// is once per update even if the window is dismissed by closing the game. Recorded either way
    /// when the notice is switched off, so turning it back on later does not produce a changelog for
    /// a version that has been running for weeks.
    /// </remarks>
    /// <returns>True if there was anything to show.</returns>
    public bool OpenForUpdate()
    {
        var since = Changelog.Since(_config.LastSeenVersion);

        _config.LastSeenVersion = Changelog.Current.ToString();
        _config.Save();

        if (since.Count == 0 || !_config.ShowChangelogOnUpdate) return false;

        _showing = since;
        IsOpen   = true;
        return true;
    }

    /// <summary>Opens on the whole history, for the button in settings.</summary>
    public void OpenAll()
    {
        _showing = Changelog.Entries;
        IsOpen   = true;
    }

    public override void Draw()
    {
        UiLayout.PushWrap();

        if (_showing.Count == 0)
        {
            ImGui.TextDisabled("Nothing recorded for this version.");
            UiLayout.PopWrap();
            return;
        }

        // The running version, so a window opened from settings still answers "which one am I on"
        ImGui.TextDisabled($"Wardrobe {Changelog.Current}");
        ImGui.Separator();
        ImGui.Spacing();

        // Scrolls on its own so the checkbox and the close button below stay put — a changelog long
        // enough to scroll is exactly the one where the way out should not move
        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2;
        ImGui.BeginChild("##changelogbody", new Vector2(0, -footer), false);

        // Again inside the child. A wrap position belongs to the window it was pushed on, so the one
        // set for the window above stops at the child's edge — and every line in here is a sentence.
        UiLayout.PushWrap();

        foreach (var entry in _showing)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.75f, 0.95f, 1f), $"Version {entry.Version}");
            ImGui.SameLine();
            ImGui.TextDisabled($" ·  {entry.Date}");
            ImGui.Spacing();

            foreach (var section in entry.Sections)
            {
                ImGui.TextUnformatted(section.Heading);
                ImGui.Spacing();

                foreach (var note in section.Notes)
                {
                    ImGui.Bullet();
                    ImGui.TextUnformatted(note.Title);

                    // Indented under its own bullet rather than run on, so a list of eight reads as
                    // eight things rather than a wall
                    ImGui.Indent();
                    ImGui.TextDisabled(note.Detail);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                ImGui.Spacing();
            }

            ImGui.Separator();
            ImGui.Spacing();
        }

        UiLayout.PopWrap();
        ImGui.EndChild();

        ImGui.Spacing();

        var show = _config.ShowChangelogOnUpdate;
        if (ImGui.Checkbox("Show this after each update", ref show))
        {
            _config.ShowChangelogOnUpdate = show;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turn this off and updates arrive quietly.\n" +
                             "Settings → Changelog can open it again at any time.");

        if (ImGui.Button("Close", new Vector2(-1, 0))) IsOpen = false;

        UiLayout.PopWrap();
    }
}
