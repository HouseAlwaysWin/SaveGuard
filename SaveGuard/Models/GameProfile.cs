using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SaveGuard.Models;

/// <summary>
/// One watched game. Everything game-specific lives here; the backup engine is
/// otherwise fully generic. A profile points at a save folder and describes how
/// snapshots of it are taken and rotated. Observable so the profile list and the
/// editor stay in sync without manual refreshing.
/// </summary>
public sealed partial class GameProfile : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");

    /// <summary>Display name, e.g. "Baldur's Gate 3 (Honour)".</summary>
    [ObservableProperty] private string _name = "New profile";

    /// <summary>The game's save folder to watch and snapshot.</summary>
    [ObservableProperty] private string _watchPath = "";

    /// <summary>Where snapshots are written. MUST be outside WatchPath.</summary>
    [ObservableProperty] private string _backupRoot = "";

    /// <summary>Watch subdirectories too. Most games need this on.</summary>
    [ObservableProperty] private bool _recursive = true;

    /// <summary>
    /// Optional comma-separated extension filter for the watcher trigger
    /// (e.g. ".lsv,.sav"). Empty = react to any change. A snapshot always copies
    /// the WHOLE folder regardless; this only decides which file changes are
    /// worth triggering an auto-backup.
    /// </summary>
    [ObservableProperty] private string _triggerExtensions = "";

    /// <summary>Keep at most this many snapshots; oldest are pruned.</summary>
    [ObservableProperty] private int _maxSnapshots = 20;

    /// <summary>Run the FileSystemWatcher and back up automatically on save.</summary>
    [ObservableProperty] private bool _autoWatch = true;

    /// <summary>Quiet period (ms) after the last write before a snapshot fires.</summary>
    [ObservableProperty] private int _debounceMs = 2000;

    [JsonIgnore]
    public IReadOnlyList<string> TriggerExtensionListValue => TriggerExtensionList();

    public IReadOnlyList<string> TriggerExtensionList()
    {
        if (string.IsNullOrWhiteSpace(TriggerExtensions))
            return Array.Empty<string>();

        var parts = TriggerExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
            list.Add(p.StartsWith('.') ? p.ToLowerInvariant() : "." + p.ToLowerInvariant());
        return list;
    }
}
