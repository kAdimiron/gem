using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Gem.Services;

public sealed record LanguageOption(string Code, string NativeName, string EnglishName)
{
    public string DisplayName => $"{NativeName} ({Code})";
}

public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string DefaultLanguageCode = "en-US";
    private const string ResourcePrefix = "Gem.Localization.Locales.";

    private static readonly Assembly ResourceAssembly = typeof(LocalizationService).Assembly;

    private static readonly IReadOnlyList<LanguageOption> AllLanguages =
    [
        new("af-ZA", "Afrikaans", "Afrikaans"),
        new("am-ET", "አማርኛ", "Amharic"),
        new("ar-SA", "العربية", "Arabic"),
        new("az-Latn-AZ", "Azərbaycan", "Azerbaijani"),
        new("be-BY", "Беларуская", "Belarusian"),
        new("bg-BG", "Български", "Bulgarian"),
        new("bn-BD", "বাংলা", "Bangla"),
        new("bs-Latn-BA", "Bosanski", "Bosnian"),
        new("ca-ES", "Català", "Catalan"),
        new("cs-CZ", "Čeština", "Czech"),
        new("cy-GB", "Cymraeg", "Welsh"),
        new("da-DK", "Dansk", "Danish"),
        new("de-DE", "Deutsch", "German"),
        new("el-GR", "Ελληνικά", "Greek"),
        new("en-GB", "English (United Kingdom)", "English (United Kingdom)"),
        new("en-US", "English (United States)", "English (United States)"),
        new("es-ES", "Español (España)", "Spanish (Spain)"),
        new("es-MX", "Español (Latinoamérica)", "Spanish (Latin America)"),
        new("et-EE", "Eesti", "Estonian"),
        new("eu-ES", "Euskara", "Basque"),
        new("fa-IR", "فارسی", "Persian"),
        new("fi-FI", "Suomi", "Finnish"),
        new("fil-PH", "Filipino", "Filipino"),
        new("fr-CA", "Français (Canada)", "French (Canada)"),
        new("fr-FR", "Français (France)", "French (France)"),
        new("ga-IE", "Gaeilge", "Irish"),
        new("gl-ES", "Galego", "Galician"),
        new("gu-IN", "ગુજરાતી", "Gujarati"),
        new("he-IL", "עברית", "Hebrew"),
        new("hi-IN", "हिन्दी", "Hindi"),
        new("hr-HR", "Hrvatski", "Croatian"),
        new("hu-HU", "Magyar", "Hungarian"),
        new("hy-AM", "Հայերեն", "Armenian"),
        new("id-ID", "Bahasa Indonesia", "Indonesian"),
        new("is-IS", "Íslenska", "Icelandic"),
        new("it-IT", "Italiano", "Italian"),
        new("ja-JP", "日本語", "Japanese"),
        new("ka-GE", "ქართული", "Georgian"),
        new("kk-KZ", "Қазақ тілі", "Kazakh"),
        new("km-KH", "ភាសាខ្មែរ", "Khmer"),
        new("kn-IN", "ಕನ್ನಡ", "Kannada"),
        new("ko-KR", "한국어", "Korean"),
        new("ky-KG", "Кыргызча", "Kyrgyz"),
        new("lb-LU", "Lëtzebuergesch", "Luxembourgish"),
        new("lo-LA", "ລາວ", "Lao"),
        new("lt-LT", "Lietuvių", "Lithuanian"),
        new("lv-LV", "Latviešu", "Latvian"),
        new("mk-MK", "Македонски", "Macedonian"),
        new("ml-IN", "മലയാളം", "Malayalam"),
        new("mn-MN", "Монгол", "Mongolian"),
        new("mr-IN", "मराठी", "Marathi"),
        new("ms-MY", "Bahasa Melayu", "Malay"),
        new("mt-MT", "Malti", "Maltese"),
        new("my-MM", "မြန်မာ", "Burmese"),
        new("nb-NO", "Norsk bokmål", "Norwegian Bokmål"),
        new("ne-NP", "नेपाली", "Nepali"),
        new("nl-NL", "Nederlands", "Dutch"),
        new("nn-NO", "Norsk nynorsk", "Norwegian Nynorsk"),
        new("pa-IN", "ਪੰਜਾਬੀ", "Punjabi"),
        new("pl-PL", "Polski", "Polish"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("pt-PT", "Português (Portugal)", "Portuguese (Portugal)"),
        new("ro-RO", "Română", "Romanian"),
        new("ru-RU", "Русский", "Russian"),
        new("si-LK", "සිංහල", "Sinhala"),
        new("sk-SK", "Slovenčina", "Slovak"),
        new("sl-SI", "Slovenščina", "Slovenian"),
        new("sq-AL", "Shqip", "Albanian"),
        new("sr-Cyrl-RS", "Српски (ћирилица)", "Serbian (Cyrillic)"),
        new("sr-Latn-RS", "Srpski (latinica)", "Serbian (Latin)"),
        new("sv-SE", "Svenska", "Swedish"),
        new("sw-KE", "Kiswahili", "Swahili"),
        new("ta-IN", "தமிழ்", "Tamil"),
        new("te-IN", "తెలుగు", "Telugu"),
        new("tg-Cyrl-TJ", "Тоҷикӣ", "Tajik"),
        new("th-TH", "ไทย", "Thai"),
        new("tk-TM", "Türkmençe", "Turkmen"),
        new("tr-TR", "Türkçe", "Turkish"),
        new("tt-RU", "Татарча", "Tatar"),
        new("uk-UA", "Українська", "Ukrainian"),
        new("ur-PK", "اردو", "Urdu"),
        new("uz-Latn-UZ", "Oʻzbekcha", "Uzbek"),
        new("vi-VN", "Tiếng Việt", "Vietnamese"),
        new("zh-CN", "中文 (简体)", "Chinese (Simplified)"),
        new("zh-HK", "中文 (香港)", "Chinese (Hong Kong)"),
        new("zh-TW", "中文 (繁體)", "Chinese (Traditional)")
    ];

    private static readonly Dictionary<string, LanguageOption> LanguageMap =
        AllLanguages.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _fallbackEnglish;
    private Dictionary<string, string> _currentStrings;

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public IReadOnlyList<LanguageOption> SupportedLanguages => AllLanguages;

    public string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLanguageCode);

    public LocalizationService()
    {
        _fallbackEnglish = LoadLocaleDictionary(DefaultLanguageCode);
        string initialCode = DetectSystemLanguageCode();
        _currentStrings = LoadLocaleDictionary(initialCode);
        CurrentLanguageCode = initialCode;
        CurrentCulture = SafeGetCulture(initialCode);
    }

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            lock (_sync)
            {
                if (_currentStrings.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
                    return value;

                if (_fallbackEnglish.TryGetValue(key, out string? fallback) && !string.IsNullOrEmpty(fallback))
                    return fallback;
            }

            return key;
        }
    }

    public string Format(string key, params object?[] args)
    {
        string template = this[key];
        try
        {
            return string.Format(CurrentCulture, template, args);
        }
        catch
        {
            return template;
        }
    }

    public string SetLanguage(string? languageCode)
    {
        string resolved = ResolveSupportedLanguageCode(languageCode);
        bool changed;
        lock (_sync)
        {
            changed = !string.Equals(CurrentLanguageCode, resolved, StringComparison.OrdinalIgnoreCase);
            _currentStrings = string.Equals(resolved, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, string>(_fallbackEnglish, StringComparer.Ordinal)
                : LoadLocaleDictionary(resolved);
            CurrentLanguageCode = resolved;
            CurrentCulture = SafeGetCulture(resolved);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        if (changed)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguageCode)));
        LanguageChanged?.Invoke();
        return resolved;
    }

    public static string ResolveSupportedLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return DetectSystemLanguageCode();

        string trimmed = languageCode.Trim();
        if (LanguageMap.TryGetValue(trimmed, out var exact))
            return exact.Code;

        try
        {
            return DetectSystemLanguageCode(CultureInfo.GetCultureInfo(trimmed));
        }
        catch
        {
            return DefaultLanguageCode;
        }
    }

    public static string DetectSystemLanguageCode(CultureInfo? uiCulture = null)
    {
        CultureInfo culture = uiCulture ?? CultureInfo.CurrentUICulture;
        string tag = culture.Name;

        if (!string.IsNullOrWhiteSpace(tag) && LanguageMap.TryGetValue(tag, out var direct))
            return direct.Code;

        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            if (tag.Contains("HK", StringComparison.OrdinalIgnoreCase))
                return "zh-HK";
            if (tag.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("MO", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            return "zh-CN";
        }

        if (tag.StartsWith("sr", StringComparison.OrdinalIgnoreCase))
        {
            return tag.Contains("Cyrl", StringComparison.OrdinalIgnoreCase)
                ? "sr-Cyrl-RS"
                : "sr-Latn-RS";
        }

        if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return tag is "en-GB" or "en-AU" or "en-NZ" or "en-IE"
                ? "en-GB"
                : "en-US";
        }

        if (tag.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(tag, "es-ES", StringComparison.OrdinalIgnoreCase)
                ? "es-ES"
                : "es-MX";
        }

        if (tag.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(tag, "fr-CA", StringComparison.OrdinalIgnoreCase)
                ? "fr-CA"
                : "fr-FR";
        }

        if (tag.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(tag, "pt-PT", StringComparison.OrdinalIgnoreCase)
                ? "pt-PT"
                : "pt-BR";
        }

        if (tag.StartsWith("az", StringComparison.OrdinalIgnoreCase))
            return "az-Latn-AZ";

        if (tag.StartsWith("uz", StringComparison.OrdinalIgnoreCase))
            return "uz-Latn-UZ";

        if (tag.StartsWith("bs", StringComparison.OrdinalIgnoreCase))
            return "bs-Latn-BA";

        if (tag.StartsWith("tg", StringComparison.OrdinalIgnoreCase))
            return "tg-Cyrl-TJ";

        string twoLetter = culture.TwoLetterISOLanguageName;
        if (string.Equals(twoLetter, "no", StringComparison.OrdinalIgnoreCase))
            return "nb-NO";

        foreach (var option in AllLanguages)
        {
            string optionIso = option.Code.Split('-')[0];
            if (string.Equals(optionIso, twoLetter, StringComparison.OrdinalIgnoreCase))
                return option.Code;
        }

        return DefaultLanguageCode;
    }

    private static CultureInfo SafeGetCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static Dictionary<string, string> LoadLocaleDictionary(string code)
    {
        string resourceName = ResourcePrefix + code + ".json";
        using Stream? stream = ResourceAssembly.GetManifestResourceStream(resourceName)
            ?? FindManifestStreamCaseInsensitive(resourceName);

        if (stream is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return dict is not null
                ? new Dictionary<string, string>(dict, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Stream? FindManifestStreamCaseInsensitive(string expectedName)
    {
        foreach (string name in ResourceAssembly.GetManifestResourceNames())
        {
            if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                return ResourceAssembly.GetManifestResourceStream(name);
        }
        return null;
    }
}
