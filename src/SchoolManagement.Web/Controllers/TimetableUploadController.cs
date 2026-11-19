using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Services.Excel;
using SchoolManagement.Web.Services.Timetables.Import;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Timetables → Upload Timetable: choose the academic year and class, upload the timetable file, review and correct what
/// was read, then save it as the class's timetable. The file is read in memory and never stored; nothing is saved until
/// the administrator presses Save (and confirms replacing the class's current timetable when it has one). The saved
/// timetable is the class's ordinary timetable (/timetable/classes/{id}), editable like any other.
/// </summary>
[Route("timetables/upload")]
public class TimetableUploadController(ITimetableImportService imports) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int? classId, int? yearId, CancellationToken ct) =>
        View(await imports.UploadPageAsync(classId, yearId, ct));

    /// <summary>Step 1 → 2: read the file and show the preview. Nothing is saved.</summary>
    [HttpPost(""), ValidateAntiForgeryToken]
    [RequestSizeLimit(SpreadsheetReader.MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> Upload(int? classId, int? yearId, IFormFile? file, CancellationToken ct)
    {
        var page = await imports.UploadPageAsync(classId, yearId, ct);
        if (classId is null) page.Error = "Choose the class this timetable belongs to.";
        else if (file is null || file.Length == 0) page.Error = file is null ? "Choose the timetable file to upload." : "The file is empty.";
        if (page.Error is not null) return UploadError(page);

        await using var stream = file!.OpenReadStream();
        var (form, error) = await imports.ReadAsync(classId!.Value, yearId, file.FileName, file.Length, stream, ct);
        if (form is null)
        {
            page.Error = error;
            return UploadError(page);
        }
        return View("Preview", await imports.PreviewAsync(form, ct));
    }

    /// <summary>Steps 2–3: the preview's buttons (check, add / remove a period or day, mark a break) and Save.</summary>
    [HttpPost("preview"), ValidateAntiForgeryToken]
    [RequestFormLimits(ValueCountLimit = 10_000)]
    public async Task<IActionResult> Preview(TimetableImportForm form, CancellationToken ct)
    {
        if (form.Op != "save") return View("Preview", await imports.PreviewAsync(form, ct));
        var (id, preview) = await imports.SaveAsync(form, ct);
        if (id is null) return View("Preview", preview);
        Toast($"The timetable of {preview.ClassName} was saved.");
        return Redirect($"/timetables/upload/{id}");
    }

    /// <summary>Step 4: what the upload created, and the lessons still waiting for a teacher.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Summary(int id, CancellationToken ct)
    {
        var summary = await imports.SummaryAsync(id, ct);
        return summary is null ? NotFound() : View("Summary", summary);
    }

    private IActionResult UploadError(TimetableUploadPage page)
    {
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return View("Index", page);
    }
}
