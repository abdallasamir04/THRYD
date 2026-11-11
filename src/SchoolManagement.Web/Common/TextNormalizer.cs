using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SchoolManagement.Web.Common;

/// <summary>
/// Cleaning (what is stored and shown) and matching keys (what duplicates are detected on) for
/// names, class names and phone numbers. Works for English and Arabic text.
/// </summary>
public static partial class TextNormalizer
{
    /// <summary>Trims and collapses every run of whitespace (including non-breaking spaces) to one space.</summary>
    public static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Whitespace().Replace(value.Trim(), " ");
    }

    /// <summary>
    /// Matching key for person names: cleaned, upper case, Arabic diacritics and tatweel removed,
    /// Alef / Yeh / Teh Marbuta variants unified, so the same Arabic name typed with or without diacritics
    /// or letter variants gives the same key (user-entered names; not UI text).
    /// </summary>
    public static string NameKey(string? value)
    {
        var text = CleanText(value);
        if (text.Length == 0) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormC))
        {
            switch (ch)
            {
                case 'ـ': continue; // tatweel
                case >= 'ً' and <= 'ْ': continue; // harakat
                case 'ٰ': continue; // superscript alef
                case 'أ' or 'إ' or 'آ' or 'ٱ': sb.Append('ا'); break;
                case 'ى': sb.Append('ي'); break;
                case 'ة': sb.Append('ه'); break;
                default: sb.Append(ch); break;
            }
        }

        return Whitespace().Replace(sb.ToString(), " ").Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Matching key for class names: upper case without spaces or separators, Arabic digits as
    /// Latin digits, and a leading "GRADE" shortened to "G". "kg 1-a", "KG1A" and "Grade 1A" → "KG1A" / "G1A".
    /// </summary>
    public static string ClassKey(string? value)
    {
        var text = ToLatinDigits(CleanText(value)).ToUpperInvariant();
        text = ClassSeparators().Replace(text, "");
        if (text.StartsWith("GRADE", StringComparison.Ordinal) && text.Length > 5 && char.IsDigit(text[5]))
            text = "G" + text[5..];
        return text;
    }

    /// <summary>Cleans a phone number for storage: Latin digits, single spaces. Never changes the digits.</summary>
    public static string CleanPhone(string? value) => ToLatinDigits(CleanText(value));

    /// <summary>Digits only (used to detect the same number written differently).</summary>
    public static string PhoneKey(string? value)
    {
        var text = ToLatinDigits(value ?? "");
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch is >= '0' and <= '9') sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Allowed: optional leading +, digits, spaces, dashes, dots, brackets; 6–15 digits.</summary>
    public static bool IsValidPhone(string? cleaned)
    {
        if (string.IsNullOrEmpty(cleaned)) return true;
        if (!PhonePattern().IsMatch(cleaned)) return false;
        var digits = PhoneKey(cleaned).Length;
        return digits is >= 6 and <= 15;
    }

    public static bool ContainsLetter(string value)
    {
        foreach (var ch in value)
            if (char.IsLetter(ch)) return true;
        return false;
    }

    public static string ToLatinDigits(string value)
    {
        if (value.Length == 0) return value;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (ch is >= '٠' and <= '٩') chars[i] = (char)('0' + (ch - '٠'));
            else if (ch is >= '۰' and <= '۹') chars[i] = (char)('0' + (ch - '۰'));
        }
        return new string(chars);
    }

    /// <summary>Key for comparing spreadsheet headers: lower case letters and digits only.</summary>
    public static string HeaderKey(string? value)
    {
        var text = NameKey(value).ToLower(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\s\-_./\\]+")]
    private static partial Regex ClassSeparators();

    [GeneratedRegex(@"^\+?[0-9][0-9 ()\-.]*[0-9]$")]
    private static partial Regex PhonePattern();
}
