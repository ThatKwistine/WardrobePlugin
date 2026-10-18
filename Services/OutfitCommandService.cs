using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

/// <summary>
/// Runs the slash commands an outfit asks for after it has been put on (#34).
/// </summary>
/// <remarks>
/// Off unless <see cref="Configuration.RunOutfitCommands"/> is on, because what it does is type into
/// the game's chat box as the player: whatever a line says is run exactly as if it had been entered
/// by hand. That is the point of the feature — another plugin's idle, a macro line — and also why it
/// is held to a short list of rules:
/// <list type="bullet">
/// <item>The text only ever comes from the user's own outfit. Outfit shares carry no commands in
/// either direction (see <see cref="WardrobeShareService"/>, which copies outfits field by field).</item>
/// <item>Only called where the user wore the outfit themselves. A login restore, a screenshot session
/// or a re-apply is nobody asking for it.</item>
/// <item>At most <see cref="MaxLines"/> lines, each a slash command, sent a moment apart so the
/// game's chat rate limit does not eat them.</item>
/// <item>Nothing starts while an earlier list is still running, so a command that wears another outfit
/// cannot chain into a loop.</item>
/// </list>
/// </remarks>
public sealed class OutfitCommandService : IDisposable
{
    public const int MaxLines  = 5;
    public const int MaxLength = 200;

    /// <summary>
    /// How long after the outfit goes on before the first line, so a redraw it triggers has
    /// finished and cannot swallow an emote.
    /// </summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LineGap = TimeSpan.FromMilliseconds(600);

    private readonly Configuration _config;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    private readonly Queue<string> _queue = new();
    private DateTime _nextAt;

    public OutfitCommandService(Configuration config, IFramework framework, IPluginLog log)
    {
        _config    = config;
        _framework = framework;
        _log       = log;
        _framework.Update += OnUpdate;
    }

    /// <summary>The lines of a command box that will actually be sent, in order.</summary>
    public static List<string> Parse(string? text) =>
        (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 1 && l[0] == '/' && l.Length <= MaxLength)
            .Take(MaxLines)
            .ToList();

    /// <summary>Queues an outfit's commands, if the option is on and nothing is already running.</summary>
    public void Run(Outfit outfit)
    {
        if (!_config.RunOutfitCommands) return;
        if (_queue.Count > 0) return;

        var lines = Parse(outfit.Commands);
        if (lines.Count == 0) return;

        foreach (var line in lines) _queue.Enqueue(line);
        _nextAt = DateTime.UtcNow + FirstDelay;
        _log.Information($"[Wardrobe] Outfit '{outfit.Name}': running {lines.Count} command(s)");
    }

    private unsafe void OnUpdate(IFramework framework)
    {
        if (_queue.Count == 0 || DateTime.UtcNow < _nextAt) return;

        // Switched off with commands waiting: drop them rather than run what the user has since refused
        if (!_config.RunOutfitCommands)
        {
            _queue.Clear();
            return;
        }

        var line = _queue.Dequeue();
        _nextAt = DateTime.UtcNow + LineGap;

        try
        {
            var module = UIModule.Instance();
            if (module == null) return;

            var text = Utf8String.FromString(line);
            try { module->ProcessChatBoxEntry(text); }
            finally { text->Dtor(true); }

            _log.Debug($"[Wardrobe] Sent command: {line}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Wardrobe] Could not send an outfit command");
            _queue.Clear();
        }
    }

    public void Dispose() => _framework.Update -= OnUpdate;
}
