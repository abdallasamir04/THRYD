using Microsoft.AspNetCore.Mvc;
using SchoolManagement.Web.Common;
using SchoolManagement.Web.Services;
using SchoolManagement.Web.ViewModels;

namespace SchoolManagement.Web.Controllers;

/// <summary>
/// Daily Operations → Tasks: every task in the school in one place (the same tasks as Management Team → Tasks, from the
/// same table and service). Creating, editing, status changes, details, attachments and history are the existing task
/// actions under /management/tasks; this page is the central list with tabs, totals, filters and sorting.
/// </summary>
[Route("tasks")]
public class TasksController(IManagementTaskService tasks, ICurrentUser currentUser) : AppController
{
    public const string Url = "/tasks";

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] TaskListQuery query, CancellationToken ct)
    {
        query.Tab = TaskTabs.Normalize(query.Tab);
        var model = new TaskIndexViewModel
        {
            Query = query,
            Result = await tasks.ListAsync(query, ct),
            Counts = await tasks.ScopeCountsAsync(query, ct),
            Tabs = await tasks.TabCountsAsync(query, ct),
            // The totals at the top: every task in the school, whatever the filters.
            Totals = await tasks.TabCountsAsync(new TaskListQuery(), ct),
            Staff = await tasks.StaffAsync(ct),
            Departments = await tasks.DepartmentsAsync(ct),
            MeId = currentUser.StaffId,
            BaseUrl = Url,
        };
        return IsAjax ? PartialView("_List", model) : View(model);
    }
}
