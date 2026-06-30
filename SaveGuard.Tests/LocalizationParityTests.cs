using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using SaveGuard.Services;
using Xunit;

namespace SaveGuard.Tests;

public class LocalizationParityTests
{
    // Every non-English locale must define exactly the same keys as en.json —
    // no missing translations, no stale leftover keys.
    [AvaloniaTheory]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void Locale_has_same_keys_as_english(string code)
    {
        var en = LoadKeys("en");
        var keys = LoadKeys(code);

        var missing = en.Except(keys).OrderBy(k => k).ToList();
        var extra = keys.Except(en).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0, $"{code} is missing keys: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"{code} has extra keys: {string.Join(", ", extra)}");
    }

    [AvaloniaFact]
    public void Every_registered_language_file_loads()
    {
        foreach (var lang in Localizer.Instance.AvailableLanguages)
            Assert.NotEmpty(LoadKeys(lang.Code));
    }

    private static HashSet<string> LoadKeys(string code)
    {
        using var s = AssetLoader.Open(new Uri($"avares://SaveGuard/Assets/i18n/{code}.json"));
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
        return dict!.Keys.ToHashSet();
    }
}
