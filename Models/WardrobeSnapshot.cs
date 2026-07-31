using System;
using System.Collections.Generic;

namespace WardrobePlugin.Models;

public class WardrobeSnapshot
{
    public string              OwnerName   { get; set; } = string.Empty;
    public List<ShareableItem> Items       { get; set; } = new();
    public HashSet<Guid>       WornItemIds { get; set; } = new();
    public DateTimeOffset      UpdatedAt   { get; set; } = DateTimeOffset.UtcNow;
}

public class ShareableItem
{
    public Guid         Id        { get; set; }
    public string       Name      { get; set; } = string.Empty;
    public string       Slot      { get; set; } = string.Empty;
    public string?      ImagePath { get; set; }
    public List<string> Tags      { get; set; } = new();
}
