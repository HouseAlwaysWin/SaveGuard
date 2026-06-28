using System;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using SaveGuard.Services;

namespace SaveGuard.Views;

/// <summary>
/// XAML markup extension for localized text: <c>{loc:Loc Section.Folders}</c>.
/// Resolves to a one-way binding onto a per-key INPC wrapper that re-raises its
/// value whenever the language changes, so the text updates live.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(LocItem.Value))
        {
            Source = new LocItem(Key),
            Mode = BindingMode.OneWay,
        };
}

/// <summary>One localized string, exposed as a bindable <see cref="Value"/> that
/// notifies on language change.</summary>
internal sealed class LocItem : INotifyPropertyChanged
{
    private readonly string _key;

    public LocItem(string key)
    {
        _key = key;
        Localizer.Instance.CultureChanged += OnCultureChanged;
    }

    public string Value => Localizer.Instance[_key];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnCultureChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}
