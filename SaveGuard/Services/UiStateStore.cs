using System;
using System.IO;
using System.Text.Json;
using SaveGuard.Models;

namespace SaveGuard.Services;

/// <summary>
/// Loads and saves app-level UI preferences as JSON under the user's app-data dir
/// (alongside profiles.json). Mirrors <see cref="ProfileStore"/>. Missing or
/// corrupt state falls back to sensible defaults rather than throwing.
/// </summary>
public sealed class UiStateStore
{
    private readonly string _dir;
    private readonly string _file;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public UiStateStore()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _dir = Path.Combine(baseDir, "SaveGuard");
        _file = Path.Combine(_dir, "ui.json");
    }

    public UiState Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(_file));
                if (state != null) return state;
            }
        }
        catch { /* fall through to defaults */ }
        return new UiState();
    }

    public void Save(UiState state)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, JsonSerializer.Serialize(state, JsonOpts));
    }
}
