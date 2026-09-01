using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace WardrobePlugin.Services;

/// <summary>
/// Answers "which emote is this animation?" for a mod-category item's
/// <see cref="Models.WardrobeItem.Replaces"/> key.
/// </summary>
/// <remarks>
/// An animation item records the <c>.pap</c> file name its mod replaces — <c>dance_male_loop</c> —
/// because that is what the mod's own files say and what two items have to agree on to displace each
/// other. It is also unreadable: nothing on the card says that mod is the Step Dance, and the only
/// way to find out has been to enable it and try.
/// <para>
/// The game answers this itself. Every row of <c>ActionTimeline</c> carries a <c>Key</c> of the form
/// <c>emote/dance_male_loop</c> — the animation path without its folder or extension — and each
/// <c>Emote</c> row points at up to seven of them, one per body type. Walking the emotes and keying
/// on the last segment of each timeline key gives exactly the map wanted, from the same data the
/// game plays the animation from.
/// </para>
/// <para>
/// Not everything under <c>emote/</c> belongs to an emote. The <c>/cpose</c> families —
/// <c>pose01..04</c>, <c>s_pose</c>, <c>j_pose</c>, <c>l_pose</c>, <c>b_pose</c> — are referenced by
/// no named <c>Emote</c> row at all, and the prefixes are not what they look like: <c>s_</c> and
/// <c>j_</c> are body-type variants on <c>emote/s_clap</c> and <c>emote/j_clap</c>, so reading them
/// as "sitting" and "ground sitting" on the pose families would be a guess dressed up as an answer.
/// Those come back unmatched, and the caller says so rather than inventing a name.
/// </para>
/// </remarks>
public class EmoteLookupService
{
    private readonly IDataManager _data;
    private readonly IPluginLog?  _log;

    /// <summary>Built on first use and kept. Null until then.</summary>
    private Dictionary<string, EmoteMatch>? _byStem;

    public EmoteLookupService(IDataManager data, IPluginLog? log = null)
    {
        _data = data;
        _log  = log;
    }

    /// <summary>What the game says an animation file belongs to.</summary>
    /// <param name="Names">
    /// Emote names using this animation. Nearly always one — four stems in the sheet are shared by
    /// two emotes (<c>bow</c> is Bow and Shut Eyes), and naming both is more honest than picking.
    /// </param>
    /// <param name="Command">The text command, when the emote has one. <c>/stepdance</c>.</param>
    public sealed record EmoteMatch(IReadOnlyList<string> Names, string? Command)
    {
        /// <summary>The emote names as one string, for a label with no room to list them.</summary>
        public string Display => string.Join(" / ", Names);
    }

    /// <summary>
    /// The emote an animation key belongs to, or null when the game data does not name one.
    /// </summary>
    /// <remarks>
    /// Takes what the Replaces field holds, in any of the forms it can hold it: a bare stem, a
    /// timeline key with its folder, or a file name with its extension. The item's stored value is
    /// the stem, but the field is free text and somebody pasting a path should still get an answer.
    /// </remarks>
    public EmoteMatch? Find(string? replaces)
    {
        if (string.IsNullOrWhiteSpace(replaces)) return null;

        var stem = Normalise(replaces);
        if (stem.Length == 0) return null;

        return Map().TryGetValue(stem, out var match) ? match : null;
    }

    /// <summary>One line naming the emote, or null when there is nothing to say.</summary>
    public string? Describe(string? replaces) =>
        Find(replaces) is { } m
            ? m.Command is { Length: > 0 } c ? $"{m.Display}  ·  {c}" : m.Display
            : null;

    /// <summary>Strips folder and extension and lowercases, leaving the part that identifies the file.</summary>
    private static string Normalise(string value)
    {
        var s = value.Trim().Replace('\\', '/');

        if (s.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];

        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];

        return s.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The stem-to-emote map, built once.
    /// </summary>
    /// <remarks>
    /// Around three hundred emotes with seven timeline slots each, so the walk is small and happens
    /// the first time an animation item is looked at rather than at load, where every user would pay
    /// for it and only some would use it. Failure is cached as an empty map: a missing sheet will
    /// not start answering later, and retrying it on every draw would be a sheet read per frame.
    /// </remarks>
    private Dictionary<string, EmoteMatch> Map()
    {
        if (_byStem != null) return _byStem;

        var map = new Dictionary<string, EmoteMatch>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var emotes = _data.GetExcelSheet<Emote>();
            if (emotes == null)
            {
                _log?.Warning("[Wardrobe] Emote sheet unavailable — animation items cannot be named");
                return _byStem = map;
            }

            foreach (var emote in emotes)
            {
                var name = emote.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var command = emote.TextCommand.IsValid
                    ? emote.TextCommand.Value.Command.ExtractText()
                    : null;

                foreach (var timeline in emote.ActionTimeline)
                {
                    // Row 0 is the sheet's "nothing here", and an emote uses as few of its seven
                    // slots as it likes
                    if (timeline.RowId == 0 || !timeline.IsValid) continue;

                    var key = timeline.Value.Key.ExtractText();
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    var stem = Normalise(key);
                    if (stem.Length == 0) continue;

                    if (map.TryGetValue(stem, out var seen))
                    {
                        // A shared stem keeps both names rather than letting whichever emote was
                        // reached first speak for the other
                        if (seen.Names.Contains(name)) continue;
                        map[stem] = seen with { Names = seen.Names.Append(name).ToList() };
                    }
                    else
                    {
                        map[stem] = new EmoteMatch(new List<string> { name }, command);
                    }
                }
            }

            _log?.Debug($"[Wardrobe] Emote lookup: {map.Count} animation file name(s) mapped");
        }
        catch (Exception ex)
        {
            // A sheet layout that has moved should cost the label, not the panel drawing it
            _log?.Warning(ex, "[Wardrobe] Building the emote lookup failed — animation items will go unnamed");
        }

        return _byStem = map;
    }
}
