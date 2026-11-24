using System.Globalization;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Common;

/// <summary>
/// WhatsApp chat links (<c>https://wa.me/{number}</c>) for stored phone numbers. The link needs the number in
/// international form, digits only (no +, spaces, brackets or dashes). Only numbers whose country is certain get a link:
/// <list type="bullet">
/// <item>an international number written with "+" or "00" (e.g. +971 50 123 4567, 0044 7700 900123);</item>
/// <item>an Egyptian mobile number, the school's own country (School:Location — Cairo, Egypt): 01X XXXX XXXX with
/// X = 0, 1, 2 or 5 (local form) or 20 1X XXXX XXXX (country code without +). Egypt's mobile format is fixed, so this is
/// not a guess.</item>
/// </list>
/// Anything else (a landline, a number of unknown country, too few digits) gets no link and is shown as plain text.
/// Opening the link only opens the chat; nothing is sent.
/// </summary>
public static class WhatsApp
{
    public const string EgyptCountryCode = "20";

    /// <summary>The number for wa.me (digits, country code first), or null when it cannot be determined with certainty.</summary>
    public static string? Number(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var text = TextNormalizer.ToLatinDigits(phone.Trim());
        // Only digits, spaces, dashes, dots, brackets and one leading "+" are a phone number.
        if (text.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-' or '.' or '(' or ')' or '+'))) return null;
        if (text.LastIndexOf('+') > 0) return null;
        var digits = TextNormalizer.PhoneKey(text);

        string international;
        if (text.StartsWith('+')) international = digits;
        else if (digits.StartsWith("00", StringComparison.Ordinal)) international = digits[2..];
        else if (digits.Length == 11 && digits.StartsWith("01", StringComparison.Ordinal) && digits[2] is '0' or '1' or '2' or '5')
            international = EgyptCountryCode + digits[1..];                                  // 010… → 2010…
        else if (digits.Length == 12 && digits.StartsWith("201", StringComparison.Ordinal) && digits[3] is '0' or '1' or '2' or '5')
            international = digits;                                                         // 2010… already international
        else return null;                                                                   // country unknown: no guess

        // E.164: 8–15 digits, never starting with 0.
        return international.Length is >= 8 and <= 15 && international[0] != '0' ? international : null;
    }

    /// <summary>https://wa.me/{number}, or null when the number gets no link.</summary>
    public static string? Link(string? phone) => Number(phone) is { } n ? "https://wa.me/" + n : null;
}

/// <summary>Labels and date rules of the student profile.</summary>
public static class StudentProfileRules
{
    public static readonly DismissalMethod[] DismissalMethods = Enum.GetValues<DismissalMethod>();

    public static string Dismissal(DismissalMethod m) => m switch
    {
        DismissalMethod.Bus => "Bus",
        DismissalMethod.Car => "Car",
        _ => m.ToString(),
    };

    public static string Icon(DismissalMethod m) => m == DismissalMethod.Bus ? "bus" : "car";

    /// <summary>Oldest accepted birth date / application date (no student of a K–12 school is older).</summary>
    public static readonly DateOnly EarliestBirth = new(1990, 1, 1);
    public static readonly DateOnly EarliestApplication = new(2000, 1, 1);

    /// <summary>"12 Mar 2019" — the date format of the student pages.</summary>
    public static string Date(DateOnly? d) => d?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Age in whole years on <paramref name="today"/>.</summary>
    public static int Age(DateOnly birth, DateOnly today)
    {
        var age = today.Year - birth.Year;
        if (today < birth.AddYears(age)) age--;
        return age;
    }
}
