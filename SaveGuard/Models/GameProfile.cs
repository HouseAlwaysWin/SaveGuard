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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _name = "New profile";

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

    /// <summary>
    /// Optional comma-separated extension filter for the save-preview image
    /// (e.g. ".png,.webp"). If a snapshot (or the live save folder) contains a
    /// file of one of these types, the newest one is shown as a thumbnail.
    /// Blank = no preview.
    /// </summary>
    [ObservableProperty] private string _imageExtensions = ".png,.jpg,.jpeg,.webp,.bmp";

    /// <summary>Keep at most this many snapshots; oldest are pruned.</summary>
    [ObservableProperty] private int _maxSnapshots = 20;

    /// <summary>Run the FileSystemWatcher and back up automatically on save.</summary>
    [ObservableProperty] private bool _autoWatch = true;

    /// <summary>Quiet period (ms) after the last write before a snapshot fires.</summary>
    [ObservableProperty] private int _debounceMs = 2000;

    /// <summary>
    /// Extra files OUTSIDE the watch folder that are captured with every snapshot
    /// and put back on restore (one per line). Each line is an absolute file path
    /// or a wildcard pattern: containing "**" = recursive (the text before "**" is
    /// the base directory, after it the filename pattern); "*"/"?" = that directory
    /// only. BG3 Honour mode keeps its run-failed flag in profile8.lsf, two levels
    /// above the save folder — restoring the save alone never clears it.
    /// </summary>
    [ObservableProperty] private string _companionFiles = "";

    /// <summary>
    /// Files INSIDE the watch folder to leave out of backups (one pattern per line).
    /// SaveGuard ignores these entirely: they are never copied into a snapshot, and a
    /// restore leaves the live copy untouched (it won't delete or overwrite them).
    /// A pattern with no "/" matches by name at any depth (e.g. <c>*.log</c>,
    /// <c>Thumbs.db</c>, or a folder name like <c>cache</c>); a pattern with "/" matches
    /// the path relative to the watch folder, where <c>**</c> spans subfolders
    /// (e.g. <c>logs/*.txt</c>, <c>cache/**</c>).
    /// </summary>
    [ObservableProperty] private string _excludePatterns = "";

    /// <summary>The Steam AppId this profile was imported from, or 0 when it was
    /// added manually. Used to avoid importing the same game twice.</summary>
    [ObservableProperty] private long _steamAppId;

    /// <summary>Optional path to an image shown as the game's icon in the list and
    /// editor. Empty = show a letter placeholder. Auto-filled from Steam on import;
    /// the user can change it or clear it back to the placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private string _iconPath = "";

    [JsonIgnore]
    public IReadOnlyList<string> TriggerExtensionListValue => TriggerExtensionList();

    /// <summary>True when a custom icon image is set.</summary>
    [JsonIgnore]
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);

    /// <summary>First character of the name, for the placeholder avatar.</summary>
    [JsonIgnore]
    public string Initial =>
        string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim().Substring(0, 1).ToUpperInvariant();

    public IReadOnlyList<string> TriggerExtensionList() => ParseExtensions(TriggerExtensions);

    public IReadOnlyList<string> ImageExtensionList() => ParseExtensions(ImageExtensions);

    /// <summary>The companion patterns, one per non-blank line (trimmed).</summary>
    public IReadOnlyList<string> CompanionFileList() => NonBlankLines(CompanionFiles);

    /// <summary>The exclude patterns, one per non-blank line (trimmed).</summary>
    public IReadOnlyList<string> ExcludeList() => NonBlankLines(ExcludePatterns);

    private static IReadOnlyList<string> NonBlankLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) list.Add(trimmed);
        }
        return list;
    }

    /// <summary>Splits a comma-separated extension list into normalized ".ext" forms.</summary>
    private static IReadOnlyList<string> ParseExtensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
            list.Add(p.StartsWith('.') ? p.ToLowerInvariant() : "." + p.ToLowerInvariant());
        return list;
    }
}
