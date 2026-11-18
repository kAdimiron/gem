using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gem.Helpers;
using Gem.Services;

namespace Gem.Tests;

[CollectionDefinition("Localization")]
public sealed class LocalizationTestCollection
{
}

[Collection("Localization")]
public sealed class LocalizationServiceTests : IDisposable
{
    private static readonly Regex PlaceholderRegex = new(@"\{[0-9]+\}", RegexOptions.Compiled);
    private readonly string _previousLanguage;
    private readonly List<string> _tempDirs = new();

    public LocalizationServiceTests()
    {
        _previousLanguage = LocalizationService.Instance.CurrentLanguageCode;
    }

    public void Dispose()
    {
        LocalizationService.Instance.SetLanguage(_previousLanguage);
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void AllSupportedLanguages_HaveCompleteEmbeddedLocaleDictionariesAndValidPlaceholders()
    {
        IReadOnlyList<LanguageOption> languages = LocalizationService.Instance.SupportedLanguages;
        Assert.Equal(86, languages.Count);

        Dictionary<string, string> enUs = ReadEmbeddedLocale("en-US");
        Assert.True(enUs.Count >= 280);

        foreach (LanguageOption lang in languages)
        {
            Dictionary<string, string> locale = ReadEmbeddedLocale(lang.Code);
            Assert.Equal(enUs.Count, locale.Count);

            foreach ((string key, string enValue) in enUs)
            {
                Assert.True(locale.TryGetValue(key, out string? translated), $"[{lang.Code}] Missing key '{key}'.");
                Assert.False(string.IsNullOrWhiteSpace(translated), $"[{lang.Code}] Empty value for key '{key}'.");

                string[] expectedPlaceholders = PlaceholderRegex.Matches(enValue).Select(m => m.Value).Distinct().OrderBy(x => x).ToArray();
                string[] actualPlaceholders = PlaceholderRegex.Matches(translated!).Select(m => m.Value).Distinct().OrderBy(x => x).ToArray();
                Assert.Equal(expectedPlaceholders, actualPlaceholders);
            }
        }
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("en-AU", "en-GB")]
    [InlineData("ru-RU", "ru-RU")]
    [InlineData("uk-UA", "uk-UA")]
    [InlineData("zh-Hans-SG", "zh-CN")]
    [InlineData("zh-Hant-MO", "zh-TW")]
    [InlineData("zh-HK", "zh-HK")]
    [InlineData("es-AR", "es-MX")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt-AO", "pt-BR")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("sr-Cyrl-RS", "sr-Cyrl-RS")]
    [InlineData("sr-Latn-BA", "sr-Latn-RS")]
    [InlineData("nb-NO", "nb-NO")]
    [InlineData("nn-NO", "nn-NO")]
    [InlineData("eo-001", "en-US")]
    public void DetectSystemLanguageCode_ResolvesSupportedCulturesAndFallbacks(string cultureName, string expectedCode)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        string resolved = LocalizationService.DetectSystemLanguageCode(culture);
        Assert.Equal(expectedCode, resolved);
    }

    [Fact]
    public void SetLanguage_UpdatesStringsFormatsAndNotifiesListeners()
    {
        var loc = LocalizationService.Instance;
        int languageChangedCount = 0;
        var changedProps = new List<string?>();

        void OnLanguageChanged() => languageChangedCount++;
        void OnPropertyChanged(object? _, PropertyChangedEventArgs e) => changedProps.Add(e.PropertyName);

        loc.LanguageChanged += OnLanguageChanged;
        loc.PropertyChanged += OnPropertyChanged;
        try
        {
            loc.SetLanguage("ru-RU");
            changedProps.Clear();
            languageChangedCount = 0;

            string resolved = loc.SetLanguage("en-US");
            Assert.Equal("en-US", resolved);
            Assert.Equal("en-US", loc.CurrentLanguageCode);
            Assert.Equal("Home", loc["NavHome"]);
            Assert.Equal("1 KB", FormatHelper.FormatBytes(1024));
            Assert.Equal(1, languageChangedCount);
            Assert.Contains("Item[]", changedProps);
            Assert.Contains(nameof(LocalizationService.CurrentLanguageCode), changedProps);

            loc.SetLanguage("ja-JP");
            Assert.Equal("ja-JP", loc.CurrentLanguageCode);
            Assert.NotEqual("Home", loc["NavHome"]);
            Assert.False(string.IsNullOrWhiteSpace(loc["NavHome"]));
        }
        finally
        {
            loc.LanguageChanged -= OnLanguageChanged;
            loc.PropertyChanged -= OnPropertyChanged;
        }
    }

    [Fact]
    public void SettingsService_FirstLaunchPopulatesDetectedSupportedLanguage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "GemLocSettingsTests_" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(tempDir);

        var service = new SettingsService(tempDir);
        var loaded = service.LoadSettings();

        Assert.False(string.IsNullOrWhiteSpace(loaded.Language));
        Assert.Contains(LocalizationService.Instance.SupportedLanguages, l => l.Code == loaded.Language);
        Assert.True(File.Exists(Path.Combine(tempDir, "settings.json")));
    }

    private static Dictionary<string, string> ReadEmbeddedLocale(string code)
    {
        var asm = typeof(LocalizationService).Assembly;
        string resourceName = $"Gem.Localization.Locales.{code}.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream!);
        Assert.NotNull(parsed);
        return parsed!;
    }
}
