using System;
using System.Linq;
using Dalamud.Plugin.Services;
using WardrobePlugin.Models;

namespace WardrobePlugin.Services;

public enum ShareConnectionState { Disconnected, Connecting, Connected }

public class WardrobeShareService : IDisposable
{
    public ShareConnectionState State     { get; private set; } = ShareConnectionState.Disconnected;
    public string?              ShareCode { get; private set; }

    public event Action? OnConnectionStateChanged;

    // Fired by the WebSocket receiver when a command arrives from the server.
    // Nothing fires it yet — the backend implementation will do so.
#pragma warning disable CS0067
    public event Action<RemoteCommand>? OnCommandReceived;
#pragma warning restore CS0067

    private readonly WardrobeService _wardrobe;
    private readonly Configuration   _config;
    private readonly IFramework      _framework;
    private readonly IPluginLog      _log;

    public WardrobeShareService(WardrobeService wardrobe, Configuration config,
        IFramework framework, IPluginLog log)
    {
        _wardrobe  = wardrobe;
        _config    = config;
        _framework = framework;
        _log       = log;

        _wardrobe.WardrobeChanged += OnWardrobeChanged;
        OnCommandReceived         += ExecuteCommand;
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public void Connect()
    {
        if (State != ShareConnectionState.Disconnected) return;

        // TODO: open WebSocket to _config.ShareServerUrl
        //       on success: set State = Connected, ShareCode = <code from server>
        //       on failure: set State = Disconnected, log error
        _log.Information("[Wardrobe Share] Backend not yet implemented — connection is a no-op.");
    }

    public void Disconnect()
    {
        if (State == ShareConnectionState.Disconnected) return;

        // TODO: close WebSocket gracefully
        State     = ShareConnectionState.Disconnected;
        ShareCode = null;
        OnConnectionStateChanged?.Invoke();
        _log.Information("[Wardrobe Share] Disconnected.");
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    public WardrobeSnapshot BuildSnapshot()
    {
        // Owner name: resolved by the server from the authenticated session once the backend exists.
        return new WardrobeSnapshot
        {
            OwnerName   = string.Empty,
            Items       = _config.WardrobeItems.Select(i => new ShareableItem
            {
                Id        = i.Id,
                Name      = i.Name,
                Slot      = i.Slot.ToString(),
                ImagePath = i.ImagePath,
                Tags      = i.Tags,
            }).ToList(),
            WornItemIds = _config.WornItems.Values.ToHashSet(),
        };
    }

    public void PushSnapshot()
    {
        if (State != ShareConnectionState.Connected) return;

        var snapshot = BuildSnapshot();

        // TODO: serialize snapshot to JSON and send over WebSocket
        //       Images should be served separately (by URL) rather than inlined,
        //       so the server needs a companion image-upload endpoint.
        _log.Debug($"[Wardrobe Share] PushSnapshot: {snapshot.Items.Count} items, " +
                   $"{snapshot.WornItemIds.Count} worn — backend not yet implemented.");
    }

    // ── Incoming commands ─────────────────────────────────────────────────────

    private void ExecuteCommand(RemoteCommand cmd)
    {
        if (!_config.ShareAllowlist.Contains(cmd.ViewerName, StringComparer.OrdinalIgnoreCase))
        {
            _log.Warning($"[Wardrobe Share] Rejected command '{cmd.Type}' from '{cmd.ViewerName}' — not in allowlist.");
            return;
        }

        _framework.RunOnFrameworkThread(() =>
        {
            _log.Information($"[Wardrobe Share] Executing '{cmd.Type}' from '{cmd.ViewerName}'.");

            switch (cmd.Type)
            {
                case RemoteCommandType.Wear when cmd.ItemId.HasValue:
                    var toWear = _config.WardrobeItems.Find(i => i.Id == cmd.ItemId.Value);
                    if (toWear != null) _wardrobe.WearItem(toWear);
                    break;

                case RemoteCommandType.Unequip when cmd.ItemId.HasValue:
                    var toRemove = _config.WardrobeItems.Find(i => i.Id == cmd.ItemId.Value);
                    if (toRemove != null) _wardrobe.UnwearItem(toRemove);
                    break;

                case RemoteCommandType.UnequipAll:
                    _wardrobe.StripAll();
                    break;

                case RemoteCommandType.RequestSnapshot:
                    PushSnapshot();
                    break;
            }
        });
    }

    private void OnWardrobeChanged() => PushSnapshot();

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _wardrobe.WardrobeChanged -= OnWardrobeChanged;
        OnCommandReceived         -= ExecuteCommand;
        Disconnect();
    }
}
