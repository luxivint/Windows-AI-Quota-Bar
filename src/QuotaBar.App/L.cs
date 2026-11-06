using System.Globalization;
using System.Text.RegularExpressions;

namespace QuotaBar.App;

/// <summary>
/// Localization. English and Turkish live inline as L.T("English", "Türkçe"); other languages are
/// Lang/xx.json files (embedded) that map the English text to the translation. Missing entries fall back to English.
/// </summary>
public static class L
{
    public static readonly (string Code, string Culture, string Name)[] Languages =
    [
        ("en", "en-US", "English"),
        ("tr", "tr-TR", "Türkçe"),
        ("de", "de-DE", "Deutsch"),
        ("es", "es-ES", "Español"),
        ("fr", "fr-FR", "Français"),
        ("pt", "pt-BR", "Português (Brasil)"),
        ("ja", "ja-JP", "日本語"),
        ("zh", "zh-CN", "简体中文"),
    ];

    public static string Lang { get; private set; } = Detect();
    public static bool Tr => Lang == "tr";
    static Dictionary<string, string> _map = Load(Lang);

    static string Detect()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    /// <summary>Picks the language: "auto" (Windows language) or a code from <see cref="Languages"/>. Call before building UI.</summary>
    public static void Init(string? setting)
    {
        var code = setting is null or "" or "auto" ? Detect() : setting;
        if (!Languages.Any(l => l.Code == code)) code = "en";
        Lang = code;
        _map = Load(code);
        var culture = CultureInfo.GetCultureInfo(Languages.First(l => l.Code == code).Culture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    static Dictionary<string, string> Load(string code)
    {
        if (code is "en" or "tr") return new();
        try
        {
            var asm = typeof(L).Assembly;
            using var s = asm.GetManifestResourceStream($"Lang.{code}.json");
            if (s is null) return new();
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { return new(); }
    }

    public static string T(string en, string tr) =>
        Lang switch { "en" => en, "tr" => tr, _ => _map.TryGetValue(en, out var v) && v.Length > 0 ? v : en };

    /// <summary>Formatted text: L.F("{0} has reset", "{0} sıfırlandı", name).</summary>
    public static string F(string en, string tr, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(en, tr), args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, en, args); } // broken translation
    }

    /// <summary>Localized countdown: "6d 3h" / "6g 3sa".</summary>
    public static string Until(DateTimeOffset t, DateTimeOffset now)
    {
        var s = QuotaBar.Core.Fmt.Until(t, now);
        if (Lang == "en") return s;
        if (s == "now") return L.T("now", "şimdi");
        return Regex.Replace(s, @"(\d+)([dhms])", m => m.Groups[1].Value + Unit(m.Groups[2].Value));
    }

    static string Unit(string u) => u switch
    {
        "d" => L.T("d", "g"),
        "h" => L.T("h", "sa"),
        "m" => L.T("m", "dk"),
        _ => L.T("s", "sn"),
    };

    /// <summary>"just now" / "5m ago" localized.</summary>
    public static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var s = QuotaBar.Core.Fmt.Ago(t, now);
        if (Lang == "en") return s;
        if (s == "just now") return L.T("just now", "az önce");
        var m = Regex.Match(s, @"^(\d+)([mhd]) ago$");
        if (!m.Success) return s;
        var n = m.Groups[1].Value;
        return m.Groups[2].Value switch
        {
            "m" => L.F("{0}m ago", "{0} dk önce", n),
            "h" => L.F("{0}h ago", "{0} sa önce", n),
            _ => L.F("{0}d ago", "{0} gün önce", n),
        };
    }

    /// <summary>Limit names that come from the providers ("Session", "Weekly Opus", "5-hour").</summary>
    public static string Window(string label)
    {
        if (Lang == "en") return label;
        return label
            .Replace("Session", L.T("Session", "Oturum"))
            .Replace("Weekly", L.T("Weekly", "Haftalık"))
            .Replace("Monthly", L.T("Monthly", "Aylık"))
            .Replace("Daily", L.T("Daily", "Günlük"))
            .Replace("-hour", L.T("-hour", " saatlik"));
    }
}
