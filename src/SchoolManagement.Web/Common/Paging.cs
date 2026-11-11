namespace SchoolManagement.Web.Common;

public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public int FirstItem => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastItem => Math.Min(Page * PageSize, TotalCount);
}

public static class Paging
{
    public static readonly int[] PageSizes = [10, 25, 50, 100];

    public static int NormalizePageSize(int size) => PageSizes.Contains(size) ? size : 25;

    public static int ClampPage(int page, int totalCount, int pageSize)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        return Math.Clamp(page, 1, pages);
    }
}

/// <summary>Outcome of a write operation; field errors map to form fields ("" = whole form).</summary>
public sealed class ServiceResult
{
    private readonly List<KeyValuePair<string, string>> _errors = [];

    public bool Succeeded => _errors.Count == 0;
    public IReadOnlyList<KeyValuePair<string, string>> Errors => _errors;
    public int? EntityId { get; private set; }

    public static ServiceResult Ok(int? id = null) => new() { EntityId = id };

    public static ServiceResult Fail(string field, string message)
    {
        var result = new ServiceResult();
        result._errors.Add(new(field, message));
        return result;
    }

    public ServiceResult AddError(string field, string message)
    {
        _errors.Add(new(field, message));
        return this;
    }
}
