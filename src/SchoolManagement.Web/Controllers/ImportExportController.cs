using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Import;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

[Route("import-export")]
public class ImportExportController(
    IImportService imports,
    IExcelExportService excel,
    IClassService classes,
    IDashboardService dashboard) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(ImportKind kind = ImportKind.Students, CancellationToken ct = default)
    {
        var data = await dashboard.GetAsync(ct);
        return View(new ImportExportViewModel
        {
            Students = data.TotalStudents,
            Teachers = data.TotalTeachers,
            ActiveClasses = data.ActiveClasses,
            Kind = kind,
            Error = TempData["ImportError"] as string,
        });
    }

    [HttpGet("template/{kind}")]
    public async Task<IActionResult> Template(ImportKind kind, CancellationToken ct)
    {
        var bytes = kind == ImportKind.Students
            ? excel.StudentTemplate(await classes.OptionsAsync(null, ct))
            : excel.TeacherTemplate();
        return File(bytes, StudentsController.ExcelContentType, $"{kind.ToString().ToLowerInvariant()}-import-template.xlsx");
    }

    [HttpPost("upload"), ValidateAntiForgeryToken]
    [RequestSizeLimit(SpreadsheetReader.MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> Upload(ImportKind kind, IFormFile? file, CancellationToken ct)
    {
        if (file is null)
        {
            TempData["ImportError"] = "Choose a file to upload.";
            return RedirectToAction(nameof(Index), new { kind });
        }

        await using var stream = file.OpenReadStream();
        var (session, error) = await imports.StartAsync(kind, file.FileName, file.Length, stream, ct);
        if (session is null)
        {
            TempData["ImportError"] = error;
            return RedirectToAction(nameof(Index), new { kind });
        }
        return RedirectToAction(nameof(Map), new { token = session.Token });
    }

    [HttpGet("{token:guid}/columns")]
    public IActionResult Map(Guid token)
    {
        var session = imports.Get(token);
        if (session is null) return Expired();
        return View(BuildMap(session));
    }

    /// <summary>Changing the sheet or header row re-detects the columns; "continue" saves the mapping.</summary>
    [HttpPost("{token:guid}/columns"), ValidateAntiForgeryToken]
    public IActionResult Map(Guid token, int sheetIndex, int headerIndex, string? intent, [FromForm] Dictionary<string, string?> map)
    {
        var session = imports.Get(token);
        if (session is null) return Expired();
        if (session.Result is not null) return RedirectToAction(nameof(Result), new { token });

        if (intent == "refresh" || sheetIndex != session.SheetIndex || headerIndex != session.HeaderIndex)
        {
            imports.SelectSheet(session, sheetIndex, headerIndex);
            return RedirectToAction(nameof(Map), new { token });
        }

        var mapping = ImportDefinitions.Fields(session.Kind).ToDictionary(
            f => f.Key,
            f => int.TryParse(map.GetValueOrDefault(f.Key), out var col) && col >= 0 ? col : (int?)null);
        var result = imports.ApplyMapping(session, mapping);
        if (result.Succeeded) return RedirectToAction(nameof(Preview), new { token });

        AddErrors(result);
        return View(BuildMap(session));
    }

    [HttpGet("{token:guid}/preview")]
    public async Task<IActionResult> Preview(Guid token, string filter = "all", CancellationToken ct = default)
    {
        var session = imports.Get(token);
        if (session is null) return Expired();
        if (session.Result is not null) return RedirectToAction(nameof(Result), new { token });
        if (!session.MappingConfirmed) return RedirectToAction(nameof(Map), new { token });

        var preview = await imports.PreviewAsync(session, ct);
        return View(new ImportPreviewViewModel { Session = session, Preview = preview, Filter = filter });
    }

    [HttpPost("{token:guid}/confirm"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(Guid token, CancellationToken ct)
    {
        var session = imports.Get(token);
        if (session is null) return Expired();
        if (!session.MappingConfirmed) return RedirectToAction(nameof(Map), new { token });

        await session.Gate.WaitAsync(ct);
        try
        {
            var result = await imports.CommitAsync(session, ct);
            Toast(result.Imported == 0
                ? "Nothing was imported. See the rows below."
                : $"{result.Imported} {(session.Kind == ImportKind.Students ? "student" : "teacher")}{(result.Imported == 1 ? "" : "s")} imported.",
                result.Imported == 0 ? "warning" : "success");
        }
        finally
        {
            session.Gate.Release();
        }
        return RedirectToAction(nameof(Result), new { token });
    }

    [HttpGet("{token:guid}/result")]
    public IActionResult Result(Guid token)
    {
        var session = imports.Get(token);
        if (session is null) return Expired();
        if (session.Result is null) return RedirectToAction(nameof(Preview), new { token });
        return View(new ImportResultViewModel { Session = session, Result = session.Result });
    }

    /// <summary>Excel list of the rows that were (or would be) skipped or failed, with row numbers and reasons.</summary>
    [HttpGet("{token:guid}/report")]
    public async Task<IActionResult> Report(Guid token, CancellationToken ct)
    {
        var session = imports.Get(token);
        if (session is null) return Expired();
        var rows = session.Result?.NotImported
            ?? (await imports.PreviewAsync(session, ct)).Rows.Where(r => r.Status != RowStatus.Ready).ToList();
        var bytes = excel.ImportReport(session.Kind, session.FileName, rows);
        return File(bytes, StudentsController.ExcelContentType, $"{session.Kind.ToString().ToLowerInvariant()}-import-report-{DateTime.Now:yyyy-MM-dd-HHmm}.xlsx");
    }

    private ImportMapViewModel BuildMap(ImportSession session)
    {
        var sheet = session.Sheet;
        var header = session.HeaderIndex >= 0 ? sheet.Rows[session.HeaderIndex] : null;
        var labels = Enumerable.Range(0, sheet.ColumnCount).Select(i =>
        {
            var letter = HeaderDetector.ColumnLetter(i);
            var title = header?.Cell(i);
            return string.IsNullOrWhiteSpace(title) ? $"Column {letter}" : $"{letter} · {title}";
        }).ToList();

        return new ImportMapViewModel
        {
            Session = session,
            Fields = ImportDefinitions.Fields(session.Kind),
            ColumnLabels = labels,
            Sample = session.DataRows.Take(6).ToList(),
            DataRowCount = session.DataRows.Count(),
        };
    }

    private IActionResult Expired()
    {
        TempData["ImportError"] = "This import is no longer available (it expires after 45 minutes without activity). Upload the file again.";
        return RedirectToAction(nameof(Index));
    }
}
