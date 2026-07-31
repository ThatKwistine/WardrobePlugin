using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

[Serializable]
public class Outfit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Outfit";
    public string Description { get; set; } = string.Empty;

    /// <summary>Local filesystem path to the preview image (PNG/JPG).</summary>
    public string? ImagePath { get; set; }

    // ── Glamourer ────────────────────────────────────────────────────────────
    /// <summary>Raw Glamourer state blob captured via GetStateBase64.</summary>
    public string? GlamourerStateBase64 { get; set; }

    // ── Penumbra ─────────────────────────────────────────────────────────────
    /// <summary>Name of the Penumbra collection the mod lives in.</summary>
    public string PenumbraCollection { get; set; } = string.Empty;

    /// <summary>Penumbra mod directory name (unique identifier for the mod).</summary>
    public string PenumbraModDirectory { get; set; } = string.Empty;

    /// <summary>Whether the mod should be enabled when wearing this outfit.</summary>
    public bool ModEnabled { get; set; } = true;

    /// <summary>Captured mod option settings: group name → selected option.</summary>
    public Dictionary<string, string> ModSettings { get; set; } = new();

    // ── Runtime state (not serialised) ────────────────────────────────────────
    [NonSerialized]
    public bool IsCurrentlyWorn;
}
