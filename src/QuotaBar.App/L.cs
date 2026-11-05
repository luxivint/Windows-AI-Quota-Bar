using System.Globalization;

namespace QuotaBar.App;

/// <summary>Tiny localization: English by default, Turkish when Windows UI language is Turkish.</summary>
public static class L
{
    public static readonly bool Tr = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static string T(string en, string tr) => Tr ? tr : en;

    /// <summary>Localized countdown: "6d 3h" / "6g 3sa".</summary>
    public static string Until(DateTimeOffset t, DateTimeOffset now)
    {
        var s = QuotaBar.Core.Fmt.Until(t, now);
        if (!Tr) return s;
        if (s == "now") return "şimdi";
        return System.Text.RegularExpressions.Regex.Replace(s, @"(\d+)([dhms])", m => m.Groups[1].Value + m.Groups[2].Value switch { "d" => "g", "h" => "sa", "m" => "dk", _ => "sn" });
    }
}
