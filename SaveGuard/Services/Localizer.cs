using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Platform;

namespace SaveGuard.Services;

/// <summary>
/// Tiny runtime localization service. Strings live in <c>Assets/i18n/{code}.json</c>
/// and are looked up by key. XAML binds <c>{loc:Loc Some.Key}</c>, which subscribes to
/// a per-key observable that re-emits whenever the language changes — so the whole UI
/// re-translates live. Missing keys fall back to English, then the key itself, so
/// nothing ever renders blank.
/// </summary>
public sealed class Localizer
{
    public static Localizer Instance { get; } = new();

    public sealed record LanguageOption(string Code, string Name);

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } = new[]
    {
        new LanguageOption("en", "English"),
        new LanguageOption("zh-Hant", "繁體中文"),
        new LanguageOption("zh-Hans", "简体中文"),
        new LanguageOption("ja", "日本語"),
    };

    private readonly Dictionary<string, Dictionary<string, string>> _all = new();
    private Dictionary<string, string> _current = new();
    private Dictionary<string, string> _fallback = new();
    private string _currentLanguage = "en";

    /// <summary>Raised after the language changes — drives the {loc:Loc} bindings and
    /// lets view-models re-emit strings they compute in code.</summary>
    public event Action? CultureChanged;

    private Localizer()
    {
        foreach (var lang in AvailableLanguages)
            _all[lang.Code] = Load(lang.Code);
        _fallback = _all.TryGetValue("en", out var en) ? en : new();
        _current = _fallback;
    }

    public string CurrentLanguage => _currentLanguage;

    public string this[string key]
    {
        get
        {
            if (_current.TryGetValue(key, out var v)) return v;
            if (_fallback.TryGetValue(key, out var f)) return f;
            return key;
        }
    }

    /// <summary>Localized + string.Format'ed (for messages with placeholders).</summary>
    public string Format(string key, params object[] args)
    {
        try { return string.Format(this[key], args); }
        catch { return this[key]; }
    }

    public void SetLanguage(string? code)
    {
        if (string.IsNullOrEmpty(code) || !_all.ContainsKey(code)) code = "en";
        if (code == _currentLanguage && _current.Count > 0) return;
        _currentLanguage = code;
        _current = _all[code];
        CultureChanged?.Invoke();
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://SaveGuard/Assets/i18n/{code}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { return new(); }
    }
}
