using System.Globalization;

namespace SchoolManagement.Web.Services.Pdf;

/// <summary>
/// The two Classes page downloads, both A4 portrait and built from the same <see cref="ClassRoster"/>:
///  - Download Summary: school statistics, a stage / grade summary and a class summary with teachers and coordinators.
///  - Download Students: one dedicated page per class — its information and the numbered list of its students (# and
///    Student Name only; no student ids).
/// </summary>
public static class ClassRosterReports
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public const string NotAssigned = "Not assigned";

    private static IReadOnlyList<PdfCell> R(params PdfCell[] cells) => cells;
    private static string N(int value) => value.ToString(C);
    // Business rules shared with the Excel workbooks (ExcelExportService.Classes), so both formats always agree.

    /// <summary>Available places: capacity minus enrolled students, never below zero.</summary>
    public static int Free(RosterClass c) => Math.Max(0, c.Capacity - c.Students.Count);
    public static int Free(IEnumerable<RosterClass> cs) => cs.Sum(Free);

    /// <summary>The stage a class's page names: the group when it is a part of the stage (Upper Elementary), else the stage (KG Stages).</summary>
    public static string StageOf(RosterStage s, RosterGroup g) => g.Title.Contains(s.Title, StringComparison.Ordinal) ? g.Title : s.Title;

    /// <summary>Every class with its stage, group and the coordinator of its group, in the Classes pages' order.</summary>
    public static IEnumerable<(RosterClass Class, string Where, string Coordinator)> Placed(ClassRoster r) =>
        r.Stages.SelectMany(s => s.Groups.SelectMany(g => g.Classes.Select(c => (c, StageOf(s, g), g.Coordinator ?? NotAssigned))))
            .Concat(r.Other.Select(c => (c, "No stage", NotAssigned)));

    private static PdfCell Status(RosterClass c) =>
        c.IsActive ? PdfCell.Status("Active", PdfTone.Success) : PdfCell.Status("Inactive", PdfTone.Muted);

    /// <summary>Full, near capacity (3 places or fewer, or 85 % used) or null.</summary>
    public static string? CapacityNote(RosterClass c) =>
        Free(c) == 0 ? "Full" : Free(c) <= 3 || c.Students.Count >= c.Capacity * 0.85 ? "Near capacity" : null;

    /// <summary>Active / Inactive with the capacity status underneath (the summary's former Capacity status column).</summary>
    private static PdfCell StatusWithCapacity(RosterClass c) => Status(c) with { Note = CapacityNote(c) };

    private static PdfCell Available(RosterClass c) =>
        new(N(Free(c)), Tone: CapacityNote(c) switch { "Full" => PdfTone.Error, null => PdfTone.Default, _ => PdfTone.Warning });

    /// <summary>The class's own grade (Pre-K, KG1, G7…), read from its name like the Classes pages do.</summary>
    public static string Grade(RosterClass c) => Common.ClassStages.GradeOf(c.Name)?.Label ?? "—";

    // ------------------------------------------------------------------ Download Summary

    public static PdfReport Summary(ClassRoster r, string schoolName, string? search, string? status)
    {
        var all = r.AllClasses.ToList();
        var students = all.Sum(c => c.Students.Count);
        var capacity = all.Sum(c => c.Capacity);
        var sections = new List<PdfSection>
        {
            new PdfSummary
            {
                Items =
                [
                    new("Classes", N(all.Count)),
                    new("Active classes", N(all.Count(c => c.IsActive)), PdfTone.Success),
                    new("Inactive classes", N(all.Count(c => !c.IsActive)), all.Any(c => !c.IsActive) ? PdfTone.Warning : PdfTone.Default),
                    new("Students enrolled", N(students), PdfTone.Primary),
                    new("Total capacity", N(capacity)),
                    new("Available places", N(Free(all))),
                ],
            },
            new PdfTable
            {
                Title = "Academic stages",
                Subtitle = "Classes, enrolment and places by stage and grade, with each group's coordinator.",
                Columns = [new("Stage", 1.25f), new("Grade / group", 1.45f), new("Classes", 0.75f, PdfAlign.End), new("Students", 0.8f, PdfAlign.End),
                    new("Capacity", 0.8f, PdfAlign.End), new("Available", 0.8f, PdfAlign.End), new("Coordinator", 1.75f)],
                Rows = r.Stages.SelectMany(s => s.Groups.Select(g => R(
                        new PdfCell(s.Title, Strong: true), new PdfCell(g.Title, g.Grades == g.Title ? null : g.Grades),
                        PdfCell.Num(g.Classes.Count), PdfCell.Num(g.Classes.Sum(c => c.Students.Count)), PdfCell.Num(g.Classes.Sum(c => c.Capacity)),
                        PdfCell.Num(Free(g.Classes)), new PdfCell(g.Coordinator ?? NotAssigned, g.CoordinatorRole))))
                    .Concat(r.Other.Count == 0 ? [] : [R(new PdfCell("Other classes", Strong: true), "No stage",
                        PdfCell.Num(r.Other.Count), PdfCell.Num(r.Other.Sum(c => c.Students.Count)), PdfCell.Num(r.Other.Sum(c => c.Capacity)),
                        PdfCell.Num(Free(r.Other)), NotAssigned)])
                    .ToList(),
                Footer = ["Total", "", N(all.Count), N(students), N(capacity), N(Free(all)), ""],
            },
            new PdfNotice
            {
                Text = "Homeroom Teachers and Co-Teachers are the current academic year's teaching teams, recorded for KG Stage classes; "
                    + "“Not assigned” means no one is recorded. Coordinators are the Management Team's stage coordinators.",
                Tone = PdfTone.Muted,
            },
        };

        foreach (var stage in r.Stages.Where(s => s.Classes.Any()))
        {
            sections.Add(ClassSummary($"Class summary — {stage.Title}",
                string.Join(" · ", stage.Groups.Where(g => g.CoordinatorRole is not null).DistinctBy(g => g.CoordinatorRole)
                    .Select(g => $"{g.CoordinatorRole}: {g.Coordinator ?? NotAssigned}")),
                stage.Groups.SelectMany(g => g.Classes.Select(c => (c, Grade(c), g.Coordinator ?? NotAssigned))).ToList()));
        }
        if (r.Other.Count > 0)
            sections.Add(ClassSummary("Class summary — other classes", "Class names outside the stage formats (Pre-K, KG1–KG2, G1 onward).",
                r.Other.Select(c => (c, "No stage", NotAssigned)).ToList()));

        return new PdfReport
        {
            Title = "Classes Summary",
            Subtitle = status switch { "active" => "Active classes", "inactive" => "Inactive classes", _ => "All classes" },
            Eyebrow = schoolName,
            Portrait = true,
            FooterShowsYear = true,
            Meta =
            [
                new("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
                new("Classes", N(all.Count)),
                new("Students", N(students)),
            ],
            Sections = sections,
            FileNameParts = ["Classes Summary", status],
        };
    }

    private static PdfTable ClassSummary(string title, string? subtitle, IReadOnlyList<(RosterClass Class, string Grade, string Coordinator)> rows) => new()
    {
        Title = title,
        Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
        Columns = [new("Class", 0.75f), new("Grade", 0.7f), new("Students", 0.92f, PdfAlign.End), new("Capacity", 0.92f, PdfAlign.End),
            new("Available", 0.97f, PdfAlign.End), new("Status", 1.12f), new("Homeroom Teacher", 1.5f), new("Co-Teacher", 1.45f), new("Coordinator", 1.45f)],
        Rows = rows.Select(x => R(
            new PdfCell(x.Class.Name, Strong: true), x.Grade, PdfCell.Num(x.Class.Students.Count), PdfCell.Num(x.Class.Capacity),
            Available(x.Class), StatusWithCapacity(x.Class), x.Class.Homeroom ?? NotAssigned, x.Class.CoTeacher ?? NotAssigned, x.Coordinator)).ToList(),
        Footer = ["Total", "", N(rows.Sum(x => x.Class.Students.Count)), N(rows.Sum(x => x.Class.Capacity)), N(rows.Sum(x => Free(x.Class))), "", "", "", ""],
    };

    // ------------------------------------------------------------------ Download Students

    public static PdfReport Students(ClassRoster r, string schoolName)
    {
        var placed = Placed(r).ToList();
        var pages = placed.Select(x => new PdfPage($"Class {x.Class.Name}",
        [
            new PdfFields
            {
                Title = $"Class {x.Class.Name}",
                Columns = 3,
                LabelWidth = 84,
                Items =
                [
                    new("Stage", x.Where),
                    new("Grade", Grade(x.Class)),
                    new("Status", Status(x.Class)),
                    new("Homeroom Teacher", x.Class.Homeroom ?? NotAssigned),
                    new("Co-Teacher", x.Class.CoTeacher ?? NotAssigned),
                    new("Stage Coordinator", x.Coordinator),
                    new("Enrolled students", new PdfCell(N(x.Class.Students.Count), Strong: true)),
                    new("Class capacity", N(x.Class.Capacity)),
                    new("Available places", N(Free(x.Class))),
                ],
            },
            new PdfTable
            {
                Title = "Students",
                Subtitle = x.Class.Students.Count == 0 ? null : "Alphabetical by full name.",
                // Large rows for a usual class; tighter rows for a long list, so its page is scaled down as little as possible.
                Comfortable = x.Class.Students.Count <= 26,
                Compact = x.Class.Students.Count > 34,
                RowNumbers = true,
                Columns = [new("Student Name", 1)],
                Rows = x.Class.Students.Select(s => R(new PdfCell(s.FullName, Strong: true))).ToList(),
                EmptyText = "No students are enrolled in this class.",
            },
        ])).ToList();

        return new PdfReport
        {
            Title = "Classes & Students Report",
            Eyebrow = schoolName,
            Portrait = true,
            CompactHeader = true,
            FooterShowsYear = true,
            Meta = [new("Classes", N(placed.Count)), new("Students", N(placed.Sum(x => x.Class.Students.Count)))],
            Pages = pages,
            Sections = [],
            FileNameParts = ["Classes and Students Report", r.YearName],
        };
    }
}
