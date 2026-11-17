using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Common;

/// <summary>
/// Royal Kings International School — 2026/2027 Academic Year Teachers' Calendar, transcribed from the school's
/// printed calendar: the quarters as printed, every event line in the printed order, and every coloured date of
/// the month grids. Loaded once into a year that has no calendar entries yet (School Calendar → "Load the
/// 2026/2027 teachers' calendar"); from then on the school edits the data like any other record.
///
/// Rules of the transcription:
///  - Titles are the printed English wording, unchanged (including "Q1 Lesson Plans Submission" in June).
///  - A date the print does not make clear is never guessed: the event keeps no date (or its printed notation,
///    e.g. "7/8") and is flagged for confirmation.
///  - Colours reproduce the print. A description is coloured where the print colours it, or where its date is
///    coloured in the grid; the print has no legend, so colours carry no meaning in code.
/// </summary>
public static class SchoolCalendarReference
{
    public const string Title = "Royal Kings International School — 2026/2027 Academic Year Teachers' Calendar";

    /// <summary>The year this transcription belongs to: names such as "2026-2027", "2026/2027", "2026–2027".</summary>
    public static bool Fits(string yearName) => yearName.Contains("2026") && yearName.Contains("2027");

    public sealed record QuarterRange(int Number, DateOnly Start, DateOnly End);
    public sealed record Entry(DateOnly ListMonth, string Title, DateOnly? Start, DateOnly? End, CalendarColor Color,
        string? DateLabel = null, string? Accent = null, bool Confirm = false, string? Notes = null);

    public static readonly IReadOnlyList<QuarterRange> Quarters =
    [
        new(1, D(8, 1), D(10, 31)),
        new(2, D(11, 1), D(1, 9)),
        new(3, D(1, 10), D(4, 3)),
        new(4, D(4, 4), D(7, 31)),
    ];

    /// <summary>August–December are 2026, January–July 2027.</summary>
    private static DateOnly D(int month, int day) => new(month >= 8 ? 2026 : 2027, month, day);
    private static DateOnly M(int month) => D(month, 1);

    private const CalendarColor None = CalendarColor.None, Yellow = CalendarColor.Yellow, Teal = CalendarColor.DarkTeal,
        Green = CalendarColor.Green, Blue = CalendarColor.Blue, Orange = CalendarColor.Orange, Red = CalendarColor.Red;

    private static Entry On(int m, int d, string title, CalendarColor c = None, string? label = null, string? accent = null, bool confirm = false, string? notes = null) =>
        new(M(m), title, D(m, d), null, c, label, accent, confirm, notes);
    private static Entry Span(int m, int d1, int d2, string title, CalendarColor c = None, string? label = null, bool confirm = false, string? notes = null) =>
        new(M(m), title, D(m, d1), D(m, d2), c, label, null, confirm, notes);
    private static Entry Undated(int m, string title, CalendarColor c = None, string? label = null, string? notes = null) =>
        new(M(m), title, null, null, c, label, null, true, notes);

    /// <summary>Every printed event line, month by month, in the printed order.</summary>
    public static readonly IReadOnlyList<Entry> Events =
    [
        // ---------------- Q1 — August 2026
        On(8, 20, "Q1 Curriculum Map", Teal),
        On(8, 25, "Q1 Lesson Plans", Teal),
        On(8, 30, "Weeks 1 & 2 PPTs", Teal),

        // ---------------- September 2026
        On(9, 1, "Diagnostic Tests Submission", Green),
        On(9, 8, "HW 1 & 2 + Quiz 1 Submission", confirm: true,
            notes: "Printed with the date 8. The 3rd is coloured green in the grid with no event of its own: confirm which date this submission is due."),
        Span(9, 7, 8, "First Day of School + Diagnostic Test", Blue, label: "7/8", confirm: true,
            notes: "Printed as 7/8 (the grid colours 6–8 blue). Confirm the first day of school."),
        On(9, 10, "Weeks 3 & 4 PPTs"),
        Span(9, 23, 24, "Aqua Day", Orange, label: "23/24"),
        On(9, 27, "Quiz 2 Submission", Green),
        On(9, 28, "Armed Forces Day Proposal"),
        On(9, 29, "Weeks 5 & 6 PPTs", Green),
        On(9, 30, "Q1 Project Proposal Submission", Green),

        // ---------------- October 2026
        On(10, 5, "World Teachers’ Day"),
        On(10, 6, "6th of October Victory"),
        On(10, 8, "Armed Forces Day"),
        On(10, 11, "Q1 Exams Submission + M.A.", Teal),
        On(10, 15, "Spelling bee"),
        On(10, 15, "Q2 Curriculum map submission", Green),
        On(10, 18, "Fire Drill / Q1 Project Submission", Red, accent: "Fire Drill"),
        Span(10, 18, 22, "Revision"),
        On(10, 22, "Q2 Lesson Plans Submission", Green),
        Span(10, 25, 29, "Q1 Exams (Submission of Grading Sheets + Graph Analysis on the same day of the exam)", Yellow),

        // ---------------- Q2 — November 2026
        On(11, 1, "Start of Q2", Blue),
        On(11, 12, "Q2 Project Proposal Submission", Teal),
        On(11, 16, "Career Day"),
        On(11, 19, "PTC"),
        Span(11, 22, 26, "Reading Week"),
        On(11, 30, "International Day"),

        // ---------------- December 2026
        On(12, 3, "Fire Drill", Red),
        On(12, 7, "Mock Exams Submission", Teal),
        On(12, 8, "Q2 Exams Submission", Teal),
        On(12, 8, "Q3 Curriculum map Submission", Teal),
        On(12, 13, "Q2 Projects Submission", Green),
        Span(12, 13, 15, "Revision"),
        On(12, 15, "Arabic Day"),
        Span(12, 16, 22, "Q2 Exams", Yellow),
        On(12, 22, "Xmas Bazaar"),
        On(12, 23, "Q3 Lesson Plans Submission", Green),
        // One break from 27 December to 9 January: listed under December, shown as continuing in January (Q2).
        new(M(12), "Xmas Break", D(12, 27), D(1, 9), None),

        // ---------------- Q3 — January 2027 (1–9 belong to Q2: the Xmas Break above)
        On(1, 10, "Start of Q3", Blue),
        On(1, 12, "Science Fair Proposal Submission", Teal),
        On(1, 13, "Quiz 1 Submission", Teal),
        Span(1, 11, 14, "Environmental Awareness"),
        On(1, 21, "Science Fair"),
        On(1, 25, "National Police Day"),
        On(1, 26, "International Day of Education (UN)"),
        On(1, 28, "National Police Day Off"),

        // ---------------- February 2027
        // One break from 31 January to 4 February, printed under February.
        new(M(2), "Reunion Break", D(1, 31), D(2, 4), None),
        On(2, 4, "World Cancer Day"),
        On(2, 8, "1st Day of Ramadan"),
        On(2, 10, "100 Days of School Proposal Submission", Teal),
        On(2, 10, "Quiz 2 Submission", Teal),
        On(2, 11, "PTC"),
        On(2, 14, "Valentine’s Day"),
        On(2, 17, "Q3 Project Proposal Submission"),
        On(2, 18, "100 Days of School"),
        On(2, 25, "Fire Drill", Red),
        On(2, 28, "Drama Day"),

        // ---------------- March 2027
        On(3, 9, "Eid El Fitr Celebration"),
        Span(3, 10, 13, "Eid El Fitr Holiday"),
        Undated(3, "Mock Exams Submission",
            notes: "The printed date is not readable. The grid colours 7–8, 15 and 18 March dark teal: confirm which is this submission."),
        On(3, 14, "International Math Day"),
        On(3, 17, "Q3 Project Submission"),
        Span(3, 21, 25, "Revision"),
        On(3, 21, "Q4 Curriculum Map Submission", Green),
        On(3, 25, "Q4 Lesson Plans Submission", Green),
        // 28 March – 1 April: listed under March, continues into April (still Q3).
        new(M(3), "Q3 Exams", D(3, 28), D(4, 1), Yellow),

        // ---------------- Q4 — April 2027 (1–3 belong to Q3)
        On(4, 4, "Start of Q4", Blue),
        On(4, 5, "Social Studies Gallery Proposal", Green),
        On(4, 6, "Quiz 1 Submission", Green),
        On(4, 11, "Spirit Week Proposal Submission"),
        On(4, 13, "Social Studies Gallery"),
        On(4, 14, "Production Day Proposal Submission"),
        Span(4, 18, 22, "Spirit Week + Art Gallery"),
        Span(4, 25, 29, "Easter Break"),

        // ---------------- May 2027
        On(5, 2, "Coptic Easter"),
        On(5, 3, "Sham El Nesim"),
        On(5, 6, "Labor Day Off"),
        On(5, 9, "Quiz 2 Submission", Green),
        On(5, 10, "Q4 Project Proposal Submission", Green),
        On(5, 13, "Eid Al-Adha Celebration"),
        Span(5, 16, 20, "Eid Al-Adha Holiday"),
        Undated(5, "Q4 Exams Submission",
            notes: "The printed date is not readable (26 May is coloured green in the grid). Confirm the date."),
        Undated(5, "Mock Exams Submission", notes: "The printed date is not readable. Confirm the date."),
        On(5, 27, "Production Day"),
        On(5, 30, "Q4 Projects Submission"),

        // ---------------- June 2027
        On(6, 3, "Fire Drill", Red),
        Span(6, 1, 3, "Revision"),
        On(6, 6, "Hijri New Year"),
        On(6, 10, "Hijri New Year Day Off"),
        Span(6, 6, 15, "Q4 Exams", Yellow),
        On(6, 20, "All Grading Sheets Submission"),
        Undated(6, "Pacing Guide Submission", Teal, label: "22: 27/28",
            notes: "Printed as “22: 27/28” (22, 27 and 28 June are coloured dark teal). Confirm the date or dates."),
        On(6, 27, "Q1 Lesson Plans Submission", Teal, confirm: true,
            notes: "Printed as Q1 in June (the plans for next year's first quarter?). Kept as printed: confirm the wording."),
        On(6, 29, "Summer Pack Submission"),

        // ---------------- July 2027: no events in the printed calendar.
    ];

    /// <summary>Every coloured date of the printed month grids (Friday / Saturday shading is not a mark).</summary>
    public static readonly IReadOnlyList<(DateOnly Date, CalendarColor Color)> Marks = BuildMarks();

    private static List<(DateOnly, CalendarColor)> BuildMarks()
    {
        var marks = new List<(DateOnly, CalendarColor)>();
        void Add(int m, CalendarColor c, params int[] days) { foreach (var d in days) marks.Add((D(m, d), c)); }
        Add(8, Teal, 20, 25, 30);
        Add(9, Green, 1, 3); Add(9, Blue, 6, 7, 8); Add(9, Orange, 23, 24); Add(9, Green, 27, 29, 30);
        Add(10, Teal, 11); Add(10, Green, 15); Add(10, Red, 18); Add(10, Green, 22); Add(10, Yellow, 25, 26, 27, 28, 29);
        Add(11, Blue, 1); Add(11, Teal, 12);
        Add(12, Green, 2); Add(12, Red, 3); Add(12, Teal, 6, 7, 8); Add(12, Green, 13); Add(12, Yellow, 16, 17, 20, 21, 22); Add(12, Green, 23);
        Add(1, Blue, 10); Add(1, Teal, 12, 13);
        Add(2, Teal, 9, 10); Add(2, Red, 25);
        Add(3, Teal, 7, 8, 15, 18); Add(3, Green, 21, 25); Add(3, Yellow, 28, 29, 30, 31);
        Add(4, Yellow, 1); Add(4, Blue, 4); Add(4, Green, 5, 6);
        Add(5, Green, 9, 10, 26);
        Add(6, Red, 3); Add(6, Yellow, 6, 7, 8, 9, 13, 14, 15); Add(6, Teal, 22, 27, 28);
        return marks;
    }
}
