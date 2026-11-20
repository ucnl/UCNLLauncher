using System.Globalization;
using System.Text.Json;

namespace UCNLLauncher.Services;

public sealed class LanguageInfo
{
    public LanguageInfo(string code, string name)
    {
        Code = code;
        Name = name;
    }

    public string Code { get; }
    public string Name { get; }
}

public sealed class LocalizationService
{
    public const string FallbackLanguage = "en";

    private const string Folder = "i18n";
    private const string NameKey = "_name";
    private const string Extension = ".json";

    private static readonly string[] DirectProbeCodes = { FallbackLanguage, "ru" };

    private sealed class State
    {
        public State(string code, Dictionary<string, string> merged)
        {
            Code = code;
            Merged = merged;
        }

        public string Code { get; }
        public Dictionary<string, string> Merged { get; }
    }

    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, string>>? _dictionaries;
    private IReadOnlyList<LanguageInfo> _languages = Array.Empty<LanguageInfo>();
    private volatile State? _state;

    public static LocalizationService Current { get; } = new();

    private LocalizationService() { }

    public event EventHandler<string>? LanguageChanged;

    public IReadOnlyList<LanguageInfo> Languages
    {
        get
        {
            EnsureLoaded();
            return _languages;
        }
    }

    public string CurrentLanguage => EnsureLoaded().Code;

    public string Preference
    {
        get
        {
            EnsureLoaded();
            var preference = AppSettings.Language;
            return preference == AppSettings.SystemValue || FindCode(preference) != null
                ? preference
                : AppSettings.SystemValue;
        }
    }

    public IReadOnlyDictionary<string, string> Strings => EnsureLoaded().Merged;

    public void Initialize() => EnsureLoaded();

    public string T(string key, params object[] args)
    {
        if (!EnsureLoaded().Merged.TryGetValue(key, out var text)) return key;
        if (args == null || args.Length == 0) return text;
        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    public bool SetPreference(string? value)
    {
        EnsureLoaded();
        if (!AppSettings.IsValidLanguage(value)) return false;
        string stored = value!;
        if (stored != AppSettings.SystemValue)
        {
            var code = FindCode(stored);
            if (code == null) return false;
            stored = code;
        }
        AppSettings.Language = stored;
        Recompute();
        return true;
    }

    public void Refresh()
    {
        EnsureLoaded();
        Recompute();
    }

    private State EnsureLoaded()
    {
        var state = _state;
        if (state != null) return state;
        lock (_gate)
        {
            if (_state != null) return _state;
            var dictionaries = LoadAll();
            _dictionaries = dictionaries;
            _languages = BuildLanguages(dictionaries);
            _state = BuildState(ResolveCode());
            return _state;
        }
    }

    private void Recompute()
    {
        string changedTo;
        lock (_gate)
        {
            var code = ResolveCode();
            if (_state != null && _state.Code == code) return;
            _state = BuildState(code);
            changedTo = code;
        }
        LanguageChanged?.Invoke(this, changedTo);
    }

    private string? FindCode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        foreach (var language in _languages)
            if (string.Equals(language.Code, value, StringComparison.OrdinalIgnoreCase))
                return language.Code;
        return null;
    }

    private string ResolveCode()
    {
        var preference = AppSettings.Language;
        if (preference != AppSettings.SystemValue)
        {
            var preferred = FindCode(preference);
            if (preferred != null) return preferred;
        }

        var tag = GetSystemLanguageTag();
        while (!string.IsNullOrEmpty(tag))
        {
            var match = FindCode(tag);
            if (match != null) return match;
            var cut = tag.LastIndexOf('-');
            tag = cut > 0 ? tag.Substring(0, cut) : null;
        }

        var fallback = FindCode(FallbackLanguage);
        if (fallback != null) return fallback;
        return _languages.Count > 0 ? _languages[0].Code : FallbackLanguage;
    }

    private State BuildState(string code)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var dictionaries = _dictionaries;
        if (dictionaries != null)
        {
            if (dictionaries.TryGetValue(FallbackLanguage, out var fallback))
                Merge(merged, fallback);
            if (code != FallbackLanguage && dictionaries.TryGetValue(code, out var current))
                Merge(merged, current);
        }
        return new State(code, merged);
    }

    private static void Merge(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (var pair in source)
            if (pair.Key != NameKey)
                target[pair.Key] = pair.Value;
    }

    private static IReadOnlyList<LanguageInfo> BuildLanguages(Dictionary<string, Dictionary<string, string>> dictionaries)
    {
        var list = new List<LanguageInfo>(dictionaries.Count);
        foreach (var pair in dictionaries)
        {
            var name = pair.Value.TryGetValue(NameKey, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : pair.Key;
            list.Add(new LanguageInfo(pair.Key, name));
        }
        list.Sort((a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Code, b.Code);
        });
        return list;
    }

    private static string NormalizeLanguageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var parts = tag.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        parts[0] = parts[0].ToLowerInvariant() switch
        {
            "iw" => "he",
            "in" => "id",
            "ji" => "yi",
            var other => other
        };
        if (parts[0] == "und") return "";
        return string.Join('-', parts);
    }

    private static string GetSystemLanguageTag()
    {
        try
        {
#if ANDROID
            var configuration = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Resources?.Configuration
                ?? Android.App.Application.Context.Resources?.Configuration;
            Java.Util.Locale? locale = null;
            if (configuration != null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                {
                    var locales = configuration.Locales;
                    if (locales != null && !locales.IsEmpty)
                        locale = locales.Get(0);
                }
                else
                {
                    locale = configuration.Locale;
                }
            }
            locale ??= Java.Util.Locale.Default;
            return NormalizeLanguageTag(locale?.ToLanguageTag());
#else
            return NormalizeLanguageTag(CultureInfo.CurrentUICulture.Name);
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] system language: {ex.Message}");
            return "";
        }
    }

    private static Dictionary<string, Dictionary<string, string>> LoadAll()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
#if ANDROID
        var codes = new List<string>();
        try
        {
            var files = Android.App.Application.Context.Assets?.List(Folder);
            if (files != null)
                foreach (var file in files)
                    if (file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                        codes.Add(file.Substring(0, file.Length - Extension.Length));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] list: {ex.Message}");
        }

        if (codes.Count == 0)
            codes.AddRange(DirectProbeCodes);

        foreach (var code in codes)
        {
            if (!AppSettings.IsValidLanguage(code) || code == AppSettings.SystemValue) continue;
            if (result.ContainsKey(code)) continue;
            var dictionary = ReadDictionary(code);
            if (dictionary != null) result[code] = dictionary;
        }
#endif
        return result;
    }

#if ANDROID
    private static Dictionary<string, string>? ReadDictionary(string code)
    {
        try
        {
            var assets = Android.App.Application.Context.Assets;
            if (assets == null) return null;

            byte[] bytes;
            using (var stream = assets.Open($"{Folder}/{code}{Extension}"))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, offset, bytes.Length - offset));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString();
                if (value != null) dictionary[property.Name] = value;
            }
            return dictionary.Count == 0 ? null : dictionary;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] read {code}: {ex.Message}");
            return null;
        }
    }
#endif
}
