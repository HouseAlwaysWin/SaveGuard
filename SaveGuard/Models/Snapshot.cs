using System;
using System.IO;

namespace SaveGuard.Models;

/// <summary>
/// One backup unit: a complete copy of a profile's save folder at a moment in
/// time, stored as a timestamped subfolder under the profile's BackupRoot.
/// </summary>
public sealed class Snapshot
{
    public required string FolderPath { get; init; }
    public DateTime TakenAt { get; init; }
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }

    /// <summary>Optional tag, e.g. "auto", "manual", "pre-restore".</summary>
    public string Label { get; init; } = "auto";

    public string TakenAtDisplay => TakenAt.ToString("yyyy-MM-dd  HH:mm:ss");

    public string SizeDisplay
    {
        get
        {
            double s = SizeBytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (s >= 1024 && i < units.Length - 1) { s /= 1024; i++; }
            return $"{s:0.#} {units[i]}";
        }
    }

    public string Summary => $"{TakenAtDisplay}    ·    {FileCount} files    ·    {SizeDisplay}    ·    {Label}";

    public string SubLine => $"{FileCount} files  ·  {SizeDisplay}  ·  {Label}";

    public string FolderName => Path.GetFileName(FolderPath);
}
