using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Shared helpers. Every page works as a normal request; when app.js calls it with fetch(), the
/// same action answers with a partial view (lists, modal forms) or JSON (saves and deletes).
/// </summary>
public abstract class AppController : Controller
{
    public const string ToastKey = "Toast";

    protected bool IsAjax => Request.IsAjax();

    /// <summary>A toast shown after the next full page load (for non-AJAX redirects).</summary>
    protected void Toast(string message, string type = "success") =>
        TempData[ToastKey] = $"{type}|{message}";

    protected void AddErrors(ServiceResult result)
    {
        foreach (var (field, message) in result.Errors) ModelState.AddModelError(field, message);
    }

    protected IActionResult AjaxSuccess(string message, string? redirect = null) =>
        Json(new { success = true, message, redirect });

    protected IActionResult AjaxFailure(string message, int status = 400)
    {
        Response.StatusCode = status;
        return Json(new { success = false, message });
    }

    /// <summary>Re-shows a form with its validation messages: the modal body for AJAX, the page otherwise.</summary>
    protected IActionResult FormView(string viewName, object model)
    {
        if (!IsAjax) return View(viewName, model);
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return PartialView(viewName, model);
    }

    protected IActionResult PageOrPartial(string viewName, object model) =>
        IsAjax ? PartialView(viewName, model) : View(viewName, model);

    protected IActionResult NotFoundResult(string message) =>
        IsAjax ? AjaxFailure(message, StatusCodes.Status404NotFound) : NotFound();
}
