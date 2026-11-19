using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SchoolManagement.Web.Data;

namespace SchoolManagement.Web.Common;

/// <summary>
/// A transaction for one service operation that joins the caller's transaction when there is one. On its own an
/// operation begins, commits and rolls back its own transaction exactly as before. Inside an outer transaction (for
/// example a timetable upload that saves periods and many lessons as one unit) <see cref="CommitAsync"/> and dispose
/// do nothing: the outer owner commits everything or rolls everything back.
/// </summary>
public sealed class DbScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? _own;

    private DbScope(IDbContextTransaction? own) => _own = own;

    /// <summary>True when this scope joined an outer transaction (it neither commits nor rolls back).</summary>
    public bool Joined => _own is null;

    public static async Task<DbScope> BeginAsync(SchoolDbContext db, IsolationLevel isolation, CancellationToken ct) =>
        new(db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(isolation, ct) : null);

    public Task CommitAsync(CancellationToken ct) => _own?.CommitAsync(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _own?.DisposeAsync() ?? ValueTask.CompletedTask;
}
