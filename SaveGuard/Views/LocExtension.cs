using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using SaveGuard.Services;

namespace SaveGuard.Views;

/// <summary>
/// XAML markup extension for localized text: <c>{loc:Loc Section.Folders}</c>.
/// Returns a one-way binding to the Localizer indexer so the text updates live
/// when the language changes.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
        };
}
