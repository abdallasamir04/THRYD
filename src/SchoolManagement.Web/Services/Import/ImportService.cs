using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Data;
using SchoolManagement.Web.Entities;
using SchoolManagement.Web.Services.Excel;

namespace SchoolManagement.Web.Services.Import;

public interface IImportService
{
    Task<(ImportSession? Session, string? Error)> StartAsync(ImportKind kind, string fileName, long length, Stream content, CancellationToken ct = default);
    ImportSession? Get(Guid token);
    /// <summary>Changes the sheet / header row and re-detects the column mapping.</summary>
    void SelectSheet(ImportSession session, int sheetIndex, int headerIndex);
    ServiceResult ApplyMapping(ImportSession session, IReadOnlyDictionary<string, int?> mapping);
    Task<ImportPreview> PreviewAsync(ImportSession session, CancellationToken ct = default);
    Task<ImportResult> CommitAsync(ImportSession session, CancellationToken ct = default);
}

/// <summary>
/// Upload → choose sheet, header row and columns → preview with validation → confirm → summary.
/// Nothing is written before the user confirms; every row is validated again at that moment, and a
/// row is never dropped without being reported (empty rows are the only rows not listed, and they are counted).
/// </summary>
public sealed class ImportService(SchoolDbContext db, IMemoryCache cache, ILogger<ImportService> logger) : IImportService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);
    private static string CacheKey(Guid token) => $"import:{token:N}";

    public async Task<(ImportSession? Session, string? Error)> StartAsync(ImportKind kind, string fileName, long length, Stream content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!SpreadsheetReader.AllowedExtensions.Contains(ext))
            return (null, "Upload an Excel workbook (.xlsx) or a CSV file (.csv). Older .xls files must be saved as .xlsx first.");
        if (length == 0) return (null, "The file is empty.");
        if (length > SpreadsheetReader.MaxFileBytes) return (null, "The file is larger than 10 MB.");

        IReadOnlyList<SheetGrid> sheets;
        try
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            sheets = SpreadsheetReader.Read(buffer, fileName);
        }
        catch (SpreadsheetReadException ex)
        {
            return (null, ex.Message);
        }

        var session = new ImportSession
        {
            Token = Guid.NewGuid(),
            Kind = kind,
            FileName = Path.GetFileName(fileName),
            Sheets = sheets,
        };

        // Start on the sheet whose header row matches the expected columns best.
        var fields = ImportDefinitions.Fields(kind);
        var best = sheets.Select((s, i) => (Index: i, Header: HeaderDetector.Detect(s, fields)))
            .OrderByDescending(x => x.Header.Score).ThenBy(x => x.Index).First();
        SelectSheet(session, best.Index, best.Header.HeaderIndex);

        cache.Set(CacheKey(session.Token), session, new MemoryCacheEntryOptions { SlidingExpiration = SessionLifetime });
        return (session, null);
    }

    public ImportSession? Get(Guid token) =>
        cache.TryGetValue(CacheKey(token), out ImportSession? session) ? session : null;

    public void SelectSheet(ImportSession session, int sheetIndex, int headerIndex)
    {
        session.SheetIndex = Math.Clamp(sheetIndex, 0, session.Sheets.Count - 1);
        var rowCount = session.Sheet.Rows.Count;
        session.HeaderIndex = Math.Clamp(headerIndex, -1, Math.Min(rowCount - 1, HeaderDetector.MaxHeaderScan - 1));
        session.Mapping = HeaderDetector.AutoMap(session.Sheet, session.HeaderIndex, ImportDefinitions.Fields(session.Kind));
        session.MappingConfirmed = false;
    }

    public ServiceResult ApplyMapping(ImportSession session, IReadOnlyDictionary<string, int?> mapping)
    {
        var fields = ImportDefinitions.Fields(session.Kind);
        var result = ServiceResult.Ok();
        var columns = session.Sheet.ColumnCount;
        var clean = new Dictionary<string, int?>();

        foreach (var field in fields)
        {
            var column = mapping.GetValueOrDefault(field.Key);
            if (column is not null && (column < 0 || column >= columns)) column = null;
            if (field.Required && column is null)
                result.AddError(field.Key, $"Choose the column that holds \"{field.Label}\".");
            clean[field.Key] = column;
        }

        foreach (var group in clean.Where(c => c.Value is not null).GroupBy(c => c.Value))
            if (group.Count() > 1)
                result.AddError("", $"The same column is used for {string.Join(" and ", group.Select(g => fields.First(f => f.Key == g.Key).Label))}. Each column can fill one field.");

        session.Mapping = clean;
        session.MappingConfirmed = result.Succeeded;
        return result;
    }

    public async Task<ImportPreview> PreviewAsync(ImportSession session, CancellationToken ct = default)
    {
        var rows = ExtractRows(session);
        if (session.Kind == ImportKind.Students) await ValidateStudentsAsync(rows, ct);
        else await ValidateTeachersAsync(rows, ct);
        return new ImportPreview { Kind = session.Kind, Rows = rows };
    }

    public async Task<ImportResult> CommitAsync(ImportSession session, CancellationToken ct = default)
    {
        // A second confirm (double click, browser refresh) returns the first result instead of importing again.
        if (session.Result is not null) return session.Result;

        var preview = await PreviewAsync(session, ct);
        var ready = preview.Rows.Where(r => r.Status == RowStatus.Ready).ToList();
        var imported = 0;

        if (ready.Count > 0)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                foreach (var row in ready) Add(session.Kind, row);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                imported = ready.Count;
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                // Someone added a matching record between preview and save: retry row by row and report each one.
                logger.LogWarning(ex, "Bulk import hit a unique index; retrying row by row");
                db.ChangeTracker.Clear();
                imported = await SaveOneByOneAsync(session.Kind, ready, ct);
            }
        }

        var result = new ImportResult
        {
            Imported = imported,
            SkippedDuplicates = preview.Rows.Count(r => r.Status == RowStatus.Duplicate),
            Failed = preview.Rows.Count(r => r.Status == RowStatus.Invalid),
            TotalRows = preview.Rows.Count,
            NotImported = preview.Rows.Where(r => r.Status != RowStatus.Ready).ToList(),
        };
        session.Result = result;
        return result;
    }

    private async Task<int> SaveOneByOneAsync(ImportKind kind, List<PreviewRow> rows, CancellationToken ct)
    {
        var saved = 0;
        foreach (var row in rows)
        {
            Add(kind, row);
            try
            {
                await db.SaveChangesAsync(ct);
                saved++;
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                row.Status = RowStatus.Duplicate;
                row.Errors.Add("A matching record was added while this import was open.");
            }
        }
        return saved;
    }

    private void Add(ImportKind kind, PreviewRow row)
    {
        var name = row.Value(ImportDefinitions.FullName);
        if (kind == ImportKind.Students)
        {
            db.Students.Add(new Student
            {
                FullName = name,
                NormalizedName = TextNormalizer.NameKey(name),
                SchoolClassId = row.ClassId!.Value,
            });
        }
        else
        {
            var phone = row.Value(ImportDefinitions.ContactNumber);
            var title = row.Value(ImportDefinitions.JobTitle);
            db.Teachers.Add(new Teacher
            {
                FullName = name,
                NormalizedName = TextNormalizer.NameKey(name),
                JobTitle = title.Length == 0 ? null : title,
                ContactNumber = phone.Length == 0 ? null : phone,
                NormalizedContact = TextNormalizer.PhoneKey(phone),
            });
        }
    }

    private static List<PreviewRow> ExtractRows(ImportSession session)
    {
        var fields = ImportDefinitions.Fields(session.Kind);
        var list = new List<PreviewRow>();
        foreach (var row in session.DataRows)
        {
            var values = new Dictionary<string, string>();
            var numericPhone = false;
            foreach (var field in fields)
            {
                var column = session.Mapping.GetValueOrDefault(field.Key);
                var raw = column is null ? "" : row.Cell(column.Value);
                values[field.Key] = field.Key == ImportDefinitions.ContactNumber
                    ? TextNormalizer.CleanPhone(raw)
                    : TextNormalizer.CleanText(raw);
                if (field.Key == ImportDefinitions.ContactNumber && column is not null && row.CellIsNumber(column.Value) && raw.Length > 0)
                    numericPhone = true;
            }

            if (values.Values.All(v => v.Length == 0)) continue; // nothing in the imported columns

            var preview = new PreviewRow { RowNumber = row.RowNumber, Values = values };
            if (numericPhone)
                preview.Warnings.Add("The phone number is stored as a number in Excel, so a leading 0 may be missing. Check it, or format the column as Text.");
            list.Add(preview);
        }
        return list;
    }

    private async Task ValidateStudentsAsync(List<PreviewRow> rows, CancellationToken ct)
    {
        var classes = await db.Classes.AsNoTracking().ToListAsync(ct);
        var byKey = classes.ToDictionary(c => c.NormalizedName);
        var existing = (await db.Students.AsNoTracking()
                .Select(s => new { s.SchoolClassId, s.NormalizedName })
                .ToListAsync(ct))
            .Select(s => (s.SchoolClassId, s.NormalizedName)).ToHashSet();
        var seen = new Dictionary<(int, string), int>();

        foreach (var row in rows)
        {
            var name = row.Value(ImportDefinitions.FullName);
            var className = row.Value(ImportDefinitions.Class);
            ValidateName(row, name, Student.NameMaxLength, "student");

            if (className.Length == 0) row.Errors.Add("Class is missing.");
            else if (!byKey.TryGetValue(TextNormalizer.ClassKey(className), out var cls))
                row.Errors.Add($"Class \"{className}\" does not exist. Use a class from the Classes page.");
            else if (!cls.IsActive)
                row.Errors.Add($"Class \"{cls.Name}\" is inactive.");
            else
            {
                row.ClassId = cls.Id;
                row.ClassName = cls.Name;
            }

            if (row.Errors.Count > 0)
            {
                row.Status = RowStatus.Invalid;
                continue;
            }

            var key = (row.ClassId!.Value, TextNormalizer.NameKey(name));
            if (existing.Contains(key))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add($"Already in {row.ClassName}.");
            }
            else if (seen.TryGetValue(key, out var firstRow))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add($"Same student as row {firstRow} of this file.");
            }
            else seen[key] = row.RowNumber;
        }
    }

    private async Task ValidateTeachersAsync(List<PreviewRow> rows, CancellationToken ct)
    {
        var existing = (await db.Teachers.AsNoTracking()
                .Select(t => new { t.NormalizedName, t.NormalizedContact, t.FullName })
                .ToListAsync(ct))
            .ToLookup(t => t.NormalizedName);
        var seen = new Dictionary<string, List<(string Phone, int Row)>>();

        foreach (var row in rows)
        {
            var name = row.Value(ImportDefinitions.FullName);
            var title = row.Value(ImportDefinitions.JobTitle);
            var phone = row.Value(ImportDefinitions.ContactNumber);
            ValidateName(row, name, StaffMember.NameMaxLength, "teacher");
            if (title.Length > StaffMember.JobTitleMaxLength)
                row.Errors.Add($"Job title is longer than {StaffMember.JobTitleMaxLength} characters.");
            if (phone.Length > StaffMember.ContactMaxLength || !TextNormalizer.IsValidPhone(phone))
                row.Errors.Add($"\"{phone}\" is not a valid phone number.");

            if (row.Errors.Count > 0)
            {
                row.Status = RowStatus.Invalid;
                continue;
            }

            var nameKey = TextNormalizer.NameKey(name);
            var phoneKey = TextNormalizer.PhoneKey(phone);
            static bool Same(string a, string b) => a.Length == 0 || b.Length == 0 || a == b;

            if (existing[nameKey].Any(t => Same(t.NormalizedContact, phoneKey)))
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add("This teacher already exists.");
            }
            else if (seen.TryGetValue(nameKey, out var earlier) && earlier.FirstOrDefault(e => Same(e.Phone, phoneKey)) is { Row: > 0 } match)
            {
                row.Status = RowStatus.Duplicate;
                row.Errors.Add($"Same teacher as row {match.Row} of this file.");
            }
            else
            {
                if (!seen.TryGetValue(nameKey, out var list)) seen[nameKey] = list = [];
                list.Add((phoneKey, row.RowNumber));
            }
        }
    }

    private static void ValidateName(PreviewRow row, string name, int max, string who)
    {
        if (name.Length == 0) row.Errors.Add($"The {who} name is missing.");
        else if (name.Length > max) row.Errors.Add($"The name is longer than {max} characters.");
        else if (!TextNormalizer.ContainsLetter(name)) row.Errors.Add($"\"{name}\" is not a name (no letters).");
    }
}
