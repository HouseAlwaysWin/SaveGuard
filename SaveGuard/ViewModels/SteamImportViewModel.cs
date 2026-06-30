using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveGuard.Services;

namespace SaveGuard.ViewModels;

/// <summary>One detected Steam game in the import dialog: its resolved (editable)
/// save path, where that came from, and whether it's already a profile.</summary>
public sealed partial class SteamGameRow : ObservableObject
{
    public SteamGame Game { get; init; } = null!;

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _name = "";

    /// <summary>The save folder to watch. Prefilled from resolution; editable, and
    /// the user can Browse to it. Empty = "not found, pick manually".</summary>
    [ObservableProperty] private string _savePath = "";

    [ObservableProperty] private SaveSource _source = SaveSource.None;
    [ObservableProperty] private bool _alreadyAdded;

    /// <summary>Localized one-line provenance/status shown under the path.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Preset carried over from the curated DB (applied to the new profile).</summary>
    public string? PresetTriggerExtensions { get; set; }
    public string? PresetCompanionFiles { get; set; }
}

/// <summary>
/// Drives the "Import from Steam" dialog: scans installed games on a background
/// thread, resolves a likely save folder for each, and hands the user's selection
/// back to the main view-model (via <see cref="GamesConfirmed"/>) to become profiles.
/// </summary>
public sealed partial class SteamImportViewModel : ViewModelBase
{
    private readonly SteamLibraryScanner _scanner;
    private readonly SaveDatabase _db;
    private readonly HashSet<long> _existing;
    private CancellationTokenSource? _cts;

    private static Localizer L => Localizer.Instance;

    public ObservableCollection<SteamGameRow> Games { get; } = new();

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _steamMissing;
    [ObservableProperty] private bool _scanned;

    /// <summary>Set by the View — opens a folder picker. Same contract as the main window.</summary>
    public Func<string, string?, Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the View — closes the dialog.</summary>
    public Action? RequestClose { get; set; }

    /// <summary>Raised when the user confirms; the parent turns these rows into profiles.</summary>
    public event Action<IReadOnlyList<SteamGameRow>>? GamesConfirmed;

    public SteamImportViewModel(SteamLibraryScanner scanner, SaveDatabase db, IEnumerable<long> existingAppIds)
    {
        _scanner = scanner;
        _db = db;
        _existing = new HashSet<long>(existingAppIds);
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (Busy) return;
        Busy = true;
        Scanned = false;
        SteamMissing = false;
        Games.Clear();
        Status = L["Steam.Scanning"];

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var rows = await Task.Run(() => ScanCore(ct), ct);
            if (ct.IsCancellationRequested) return;

            if (rows == null)
            {
                SteamMissing = true;
                Status = L["Steam.NotInstalled"];
                return;
            }

            foreach (var r in rows) Games.Add(r);
            Scanned = true;
            Status = rows.Count == 0 ? L["Steam.NoGames"] : L.Format("Steam.Found", rows.Count);
        }
        catch (OperationCanceledException) { /* superseded or window closed */ }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }

    // Runs entirely off the UI thread.
    private List<SteamGameRow>? ScanCore(CancellationToken ct)
    {
        var root = _scanner.FindSteamRoot();
        if (root == null) return null;

        var userIds = _scanner.EnumerateSteamUserIds(root);
        var resolver = new SaveLocationResolver(root, userIds, _db);
        var games = _scanner.EnumerateInstalledGames(root);

        var rows = new List<SteamGameRow>(games.Count);
        foreach (var g in games)
        {
            ct.ThrowIfCancellationRequested();
            var best = resolver.Resolve(g).FirstOrDefault();
            var already = _existing.Contains(g.AppId);
            rows.Add(new SteamGameRow
            {
                Game = g,
                Name = g.Name,
                SavePath = best?.Path ?? "",
                Source = best?.Source ?? SaveSource.None,
                AlreadyAdded = already,
                Selected = best != null && !already,
                PresetTriggerExtensions = best?.TriggerExtensions,
                PresetCompanionFiles = best?.CompanionFiles,
                StatusText = StatusFor(best?.Source ?? SaveSource.None, already),
            });
        }
        return rows;
    }

    private static string StatusFor(SaveSource src, bool already)
    {
        if (already) return L["Steam.AlreadyAdded"];
        return src switch
        {
            SaveSource.KnownDatabase => L["Steam.ResolvedFrom.Database"],
            SaveSource.SteamCloud => L["Steam.ResolvedFrom.Cloud"],
            SaveSource.Heuristic => L["Steam.ResolvedFrom.Heuristic"],
            _ => L["Steam.SaveNotFound"],
        };
    }

    [RelayCommand]
    private async Task BrowseSave(SteamGameRow? row)
    {
        if (row == null || PickFolder == null) return;
        var start = string.IsNullOrWhiteSpace(row.SavePath) ? row.Game.InstallDir : row.SavePath;
        var path = await PickFolder(L["Picker.WatchTitle"], start);
        if (path == null) return;
        row.SavePath = path;
        if (!row.AlreadyAdded) row.Selected = true;
    }

    [RelayCommand]
    private void SelectAll()
    {
        // Toggle: if anything addable is unselected, select all; else clear.
        var turnOn = Games.Any(g => !g.AlreadyAdded && !g.Selected);
        foreach (var g in Games)
            if (!g.AlreadyAdded) g.Selected = turnOn;
    }

    [RelayCommand]
    private void AddSelected()
    {
        var chosen = Games
            .Where(g => g.Selected && !g.AlreadyAdded && !string.IsNullOrWhiteSpace(g.SavePath))
            .ToList();
        if (chosen.Count == 0) return;
        GamesConfirmed?.Invoke(chosen);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    /// <summary>Cancel any in-flight scan (called when the window closes).</summary>
    public void CancelScan() => _cts?.Cancel();
}
