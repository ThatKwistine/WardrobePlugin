using System;

namespace WardrobePlugin.Models;

public enum RemoteCommandType
{
    Wear,
    Unequip,
    UnequipAll,
    RequestSnapshot,
}

public class RemoteCommand
{
    public RemoteCommandType Type       { get; set; }
    public Guid?             ItemId     { get; set; }
    public string            ViewerName { get; set; } = string.Empty;
}
