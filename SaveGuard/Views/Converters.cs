using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SaveGuard.Views;

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
