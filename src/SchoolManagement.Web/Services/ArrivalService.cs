using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

public sealed record ArrivalEntry(
    int StudentId, ArrivalStatus? Status, TimeOnly? Time, ArrivalMethod? Method, string? Location,
    int? ReceivedByStaffId, bool ReceivingConfirmed, string? Notes);

public sealed record ArrivalRow(
    int? RecordId, int StudentId, string StudentName, int ClassId, string ClassName, DateOnly Date,
    ArrivalStatus? Status, TimeOnly? Time, ArrivalMethod? Method, string? Location, LateStatus? Late, TimeOnly? DeadlineUsed,
    int? ReceivedById, string? ReceivedByName, bool Confirmed, string? Notes, string? RecordedBy, DateTime? RecordedAt, int Corrections)
{
    public bool IsRecorded => RecordId is not null;
}

/// <summary>Counts for recorded days only: a student with no record is "not recorded", never absent.</summary>
public sealed record ArrivalSummary(int Students, int Recorded, int Present, int Absent, int Late, int OnTime, int LateUnknown)
{
    public int NotRecorded => Math.Max(0, Students - Recorded);

    /// <summary>On time ÷ (on time + late); null when no timed arrivals are recorded.</summary>
    public double? OnTimePercent => OnTime + Late == 0 ? null : Math.Round(OnTime * 100.0 / (OnTime + Late), 1);
}

public sealed record ArrivalChecklist(DateOnly Date, int? ClassId, TimeOnly? Deadline, IReadOnlyList<ArrivalRow> Rows, ArrivalSummary Summary);

public sealed record ArrivalCorrectionRow(int Id, int RecordId, DateOnly Date, string Before, string After, string Reason, DateTime CorrectedAt, string? CorrectedBy);

public sealed record StudentArrivalHistory(
    IReadOnlyList<ArrivalRow> Records, ArrivalSummary Summary,
    IReadOnlyList<(string Label, int Count)> Methods, IReadOnlyList<(string Name, int Count)> Receivers,
    IReadOnlyList<ArrivalCorrectionRow> Corrections);

public sealed record ArrivalSaveResult(int StudentId, bool Saved, string? Error);

public interface IArrivalService
{
    Task<ArrivalChecklist> ChecklistAsync(DateOnly date, int? classId, CancellationToken ct = default);
    Task<ArrivalSummary> DaySummaryAsync(DateOnly date, CancellationToken ct = default);
    Task<IReadOnlyList<ArrivalSaveResult>> RecordAsync(DateOnly date, IReadOnlyList<ArrivalEntry> entries, CancellationToken ct = default);
    Task<ArrivalRow?> GetAsync(int recordId, CancellationToken ct = default);
    Task<ServiceResult> CorrectAsync(int recordId, ArrivalEntry entry, string? reason, CancellationToken ct = default);
    Task<StudentArrivalHistory> StudentHistoryAsync(int studentId, DateOnly? from, DateOnly? to, bool newestFirst, CancellationToken ct = default);
}

public static class Arrivals
{
    public static string Method(ArrivalMethod? m) => m switch
    {
        ArrivalMethod.SchoolBus => "School bus",
        ArrivalMethod.PrivateCar => "Private car",
        ArrivalMethod.ParentDropOff => "Parent drop-off",
        _ => "Not recorded",
    };

    /// <summary>Late = arrived after the deadline (an arrival exactly at the deadline is on time).</summary>
    public static LateStatus? LateOf(TimeOnly? time, TimeOnly? deadline) =>
        time is { } t && deadline is { } d ? (t > d ? LateStatus.Late : LateStatus.OnTime) : null;
}

public sealed class ArrivalService(SchoolDbContext db, ISettingsService settings, ICurrentUser currentUser) : IArrivalService
{
    public async Task<ArrivalChecklist> ChecklistAsync(DateOnly date, int? classId, CancellationToken ct = default)
    {
        var students = await ExpectedStudentsAsync(date, classId, ct);
        var ids = students.Select(s => s.Id).ToList();
        var records = (await Project(db.StudentArrivals.Where(r => r.ArrivalDate == date && ids.Contains(r.StudentId))).ToListAsync(ct))
            .ToDictionary(r => r.StudentId);
        var rows = students.Select(s => records.TryGetValue(s.Id, out var r) ? r
                : new ArrivalRow(null, s.Id, s.FullName, s.ClassId, s.ClassName, date, null, null, null, null, null, null, null, null, false, null, null, null, 0))
            .ToList();
        return new ArrivalChecklist(date, classId, await settings.GetArrivalDeadlineAsync(ct), rows, Summarise(rows.Count, records.Values));
    }

    public async Task<ArrivalSummary> DaySummaryAsync(DateOnly date, CancellationToken ct = default)
    {
        var expected = (await ExpectedStudentsAsync(date, null, ct)).Count;
        var records = await Project(db.StudentArrivals.Where(r => r.ArrivalDate == date)).ToListAsync(ct);
        return Summarise(expected, records);
    }

    public async Task<IReadOnlyList<ArrivalSaveResult>> RecordAsync(DateOnly date, IReadOnlyList<ArrivalEntry> entries, CancellationToken ct = default)
    {
        var results = new List<ArrivalSaveResult>();
        if (date > SchoolWeek.Today) return entries.Select(e => new ArrivalSaveResult(e.StudentId, false, "Arrivals cannot be recorded for a future date.")).ToList();
        var deadline = await settings.GetArrivalDeadlineAsync(ct);

        foreach (var entry in entries.DistinctBy(e => e.StudentId))
        {
            var student = await db.Students.AsNoTracking().Where(s => s.Id == entry.StudentId)
                .Select(s => new { s.Id, s.SchoolClassId }).FirstOrDefaultAsync(ct);
            if (student is null) { results.Add(new(entry.StudentId, false, "This student no longer exists.")); continue; }

            var existing = await db.StudentArrivals.AsNoTracking().Where(r => r.StudentId == entry.StudentId && r.ArrivalDate == date)
                .Select(r => new { r.Status, r.ArrivalTime, r.RecordedBy }).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                results.Add(new(entry.StudentId, false,
                    $"Already recorded ({existing.Status}{(existing.ArrivalTime is { } t ? " " + t.ToString("HH:mm") : "")}{(existing.RecordedBy is null ? "" : " by " + existing.RecordedBy)}). Use Correct to change it."));
                continue;
            }

            var error = await ValidateAsync(entry, ct);
            if (error is not null) { results.Add(new(entry.StudentId, false, error)); continue; }

            var record = new StudentArrivalRecord
            {
                StudentId = entry.StudentId,
                SchoolClassId = student.SchoolClassId,
                ArrivalDate = date,
                RecordedBy = currentUser.Name,
            };
            Apply(record, entry, deadline);
            db.StudentArrivals.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
                results.Add(new(entry.StudentId, true, null));
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                results.Add(new(entry.StudentId, false, "Someone else recorded this student a moment ago. Refresh to see it."));
            }
        }
        return results;
    }

    public Task<ArrivalRow?> GetAsync(int recordId, CancellationToken ct = default) =>
        Project(db.StudentArrivals.Where(r => r.Id == recordId)).FirstOrDefaultAsync(ct);

    public async Task<ServiceResult> CorrectAsync(int recordId, ArrivalEntry entry, string? reason, CancellationToken ct = default)
    {
        var record = await db.StudentArrivals.Include(r => r.ReceivedBy).FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null) return ServiceResult.Fail("", "This arrival record no longer exists.");
        var why = TextNormalizer.CleanText(reason);
        if (why.Length == 0) return ServiceResult.Fail("Reason", "Explain why the record is being corrected.");
        if (why.Length > StudentArrivalCorrection.ReasonMaxLength) return ServiceResult.Fail("Reason", $"Use {StudentArrivalCorrection.ReasonMaxLength} characters or fewer.");
        if (await ValidateAsync(entry with { StudentId = record.StudentId }, ct) is { } error) return ServiceResult.Fail("", error);

        var before = Describe(record, record.ReceivedBy?.FullName);
        Apply(record, entry, await settings.GetArrivalDeadlineAsync(ct));
        var receiver = record.ReceivedByStaffId is { } rid ? await db.StaffMembers.Where(s => s.Id == rid).Select(s => s.FullName).FirstOrDefaultAsync(ct) : null;
        var after = Describe(record, receiver);
        if (before == after) return ServiceResult.Fail("", "Nothing was changed.");

        db.StudentArrivalCorrections.Add(new StudentArrivalCorrection
        {
            StudentArrivalRecordId = record.Id,
            Before = Trim(before),
            After = Trim(after),
            Reason = why,
            CorrectedAt = DateTime.UtcNow,
            CorrectedBy = currentUser.Name,
        });
        await db.SaveChangesAsync(ct);
        return ServiceResult.Ok(record.Id);
    }

    public async Task<StudentArrivalHistory> StudentHistoryAsync(int studentId, DateOnly? from, DateOnly? to, bool newestFirst, CancellationToken ct = default)
    {
        var q = db.StudentArrivals.Where(r => r.StudentId == studentId);
        if (from is { } f) q = q.Where(r => r.ArrivalDate >= f);
        if (to is { } t) q = q.Where(r => r.ArrivalDate <= t);
        var records = await Project(q).ToListAsync(ct);
        records = (newestFirst ? records.OrderByDescending(r => r.Date) : records.OrderBy(r => r.Date)).ToList();

        var methods = records.Where(r => r.Status == ArrivalStatus.Present)
            .GroupBy(r => Arrivals.Method(r.Method)).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        var receivers = records.Where(r => r.ReceivedByName is not null)
            .GroupBy(r => r.ReceivedByName!).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        var recordIds = records.Select(r => r.RecordId!.Value).ToList();
        var corrections = await db.StudentArrivalCorrections.AsNoTracking().Where(c => recordIds.Contains(c.StudentArrivalRecordId))
            .OrderByDescending(c => c.CorrectedAt)
            .Select(c => new ArrivalCorrectionRow(c.Id, c.StudentArrivalRecordId, c.Record.ArrivalDate, c.Before, c.After, c.Reason, c.CorrectedAt, c.CorrectedBy))
            .ToListAsync(ct);

        return new StudentArrivalHistory(records, Summarise(records.Count, records), methods, receivers, corrections);
    }

    // ------------------------------------------------------------------ rules

    private async Task<string?> ValidateAsync(ArrivalEntry e, CancellationToken ct)
    {
        if (e.Status is null || !Enum.IsDefined(e.Status.Value)) return "Choose Present or Absent.";
        if (e.Status == ArrivalStatus.Absent) return TextNormalizer.CleanText(e.Notes).Length > StudentArrivalRecord.NotesMaxLength ? "Notes are too long." : null;

        if (e.Time is null) return "Enter the arrival time.";
        if (e.Method is { } m && !Enum.IsDefined(m)) return "Choose School bus, Private car or Parent drop-off.";
        if (TextNormalizer.CleanText(e.Location).Length > StudentArrivalRecord.LocationMaxLength) return "The arrival location is too long.";
        if (TextNormalizer.CleanText(e.Notes).Length > StudentArrivalRecord.NotesMaxLength) return "Notes are too long.";
        if (e.ReceivedByStaffId is { } staffId && !await db.StaffMembers.AnyAsync(s => s.Id == staffId, ct)) return "Choose the receiving staff member from the list.";
        if (e.ReceivingConfirmed && e.ReceivedByStaffId is null) return "Choose who received the student before confirming.";
        return null;
    }

    private static void Apply(StudentArrivalRecord r, ArrivalEntry e, TimeOnly? deadline)
    {
        var notes = TextNormalizer.CleanText(e.Notes);
        r.Status = e.Status!.Value;
        r.Notes = notes.Length == 0 ? null : notes;
        if (r.Status == ArrivalStatus.Absent)
        {
            // An absent student has no arrival details.
            r.ArrivalTime = null;
            r.Method = null;
            r.ArrivalLocation = null;
            r.LateStatus = null;
            r.DeadlineUsed = null;
            r.ReceivedByStaffId = null;
            r.ReceivingConfirmed = false;
            return;
        }
        var location = TextNormalizer.CleanText(e.Location);
        r.ArrivalTime = e.Time;
        r.Method = e.Method;
        r.ArrivalLocation = location.Length == 0 ? null : location;
        r.DeadlineUsed = deadline;
        r.LateStatus = Arrivals.LateOf(e.Time, deadline);
        r.ReceivedByStaffId = e.ReceivedByStaffId;
        r.ReceivingConfirmed = e.ReceivingConfirmed && e.ReceivedByStaffId is not null;
    }

    private static string Describe(StudentArrivalRecord r, string? receiver)
    {
        if (r.Status == ArrivalStatus.Absent) return "Absent" + (r.Notes is null ? "" : $" · notes: {r.Notes}");
        var parts = new List<string>
        {
            $"Present {r.ArrivalTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}"
            + (r.LateStatus is { } l ? $" ({(l == LateStatus.Late ? "late" : "on time")}, deadline {r.DeadlineUsed:HH\\:mm})" : " (no deadline set)"),
            Arrivals.Method(r.Method),
        };
        if (r.ArrivalLocation is not null) parts.Add(r.ArrivalLocation);
        if (receiver is not null) parts.Add($"received by {receiver}{(r.ReceivingConfirmed ? " (confirmed)" : "")}");
        if (r.Notes is not null) parts.Add($"notes: {r.Notes}");
        return string.Join(" · ", parts);
    }

    private static string Trim(string s) => s.Length > StudentArrivalCorrection.TextMaxLength ? s[..StudentArrivalCorrection.TextMaxLength] : s;

    private static ArrivalSummary Summarise(int students, IEnumerable<ArrivalRow> recorded)
    {
        var list = recorded.Where(r => r.IsRecorded).ToList();
        var present = list.Where(r => r.Status == ArrivalStatus.Present).ToList();
        return new ArrivalSummary(students, list.Count, present.Count,
            list.Count(r => r.Status == ArrivalStatus.Absent),
            present.Count(r => r.Late == LateStatus.Late),
            present.Count(r => r.Late == LateStatus.OnTime),
            present.Count(r => r.Late is null));
    }

    /// <summary>Students in the directory on that date (added on or before it), optionally of one class.</summary>
    private async Task<List<(int Id, string FullName, int ClassId, string ClassName)>> ExpectedStudentsAsync(DateOnly date, int? classId, CancellationToken ct)
    {
        var q = db.Students.AsNoTracking();
        if (classId is { } c) q = q.Where(s => s.SchoolClassId == c);
        var list = await q.OrderBy(s => s.SchoolClass.SortOrder).ThenBy(s => s.FullName)
            .Select(s => new { s.Id, s.FullName, s.SchoolClassId, ClassName = s.SchoolClass.Name, s.CreatedAt })
            .ToListAsync(ct);
        return list.Where(s => DateOnly.FromDateTime(s.CreatedAt.ToLocalTime()) <= date)
            .Select(s => (s.Id, s.FullName, s.SchoolClassId, s.ClassName)).ToList();
    }

    private static IQueryable<ArrivalRow> Project(IQueryable<StudentArrivalRecord> q) =>
        q.AsNoTracking().Select(r => new ArrivalRow(r.Id, r.StudentId, r.Student.FullName, r.SchoolClassId, r.SchoolClass.Name, r.ArrivalDate,
            r.Status, r.ArrivalTime, r.Method, r.ArrivalLocation, r.LateStatus, r.DeadlineUsed,
            r.ReceivedByStaffId, r.ReceivedBy != null ? r.ReceivedBy.FullName : null, r.ReceivingConfirmed, r.Notes, r.RecordedBy, r.CreatedAt,
            r.Corrections.Count));
}
