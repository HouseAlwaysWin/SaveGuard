using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SaveGuard.Views;

/// <summary>Loads an image file path into a cached <see cref="Bitmap"/> for icons.
/// Returns null for blank/missing paths so the placeholder shows through.</summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    public static readonly PathToBitmapConverter Instance = new();

    // Decode each distinct path once; icons are reused across the list and editor.
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        Bitmap? bmp = null;
        try
        {
            // Icons show at 30–44 px; decode at 160 px (sharp even at high DPI) rather than
            // the source resolution, so a 256/512 px Steam icon isn't held full-size in RAM.
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                bmp = Bitmap.DecodeToWidth(fs, 160);
            }
        }
        catch { bmp = null; }
        Cache[path] = bmp;
        return bmp;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a snapshot label ("auto"/"manual"/"pre-restore") to a badge brush.</summary>
public sealed class LabelToBrushConverter : IValueConverter
{
    public static readonly LabelToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = value as string ?? "auto";
        return label switch
        {
            "manual" => new SolidColorBrush(Color.Parse("#E0A24E")),       // gold
            "pre-restore" => new SolidColorBrush(Color.Parse("#D9756B")),  // coral
            _ => new SolidColorBrush(Color.Parse("#6FB58A")),              // green (auto)
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True -> watching green, otherwise muted grey, for the status dot.</summary>
public sealed class WatchLabelToBrushConverter : IValueConverter
{
    public static readonly WatchLabelToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.StartsWith("Watching")) return new SolidColorBrush(Color.Parse("#6FB58A"));
        if (s.Contains("off")) return new SolidColorBrush(Color.Parse("#8B93A0"));
        return new SolidColorBrush(Color.Parse("#E0A24E"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
