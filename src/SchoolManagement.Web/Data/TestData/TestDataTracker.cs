using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Data.TestData;

/// <summary>
/// Used only by the test-data commands. While the seeder runs it
/// (1) refuses any change or deletion of a record that is not generated test data, so existing records
///     cannot be modified, and
/// (2) records the key of every row the application adds, in TestDataRegistry, as part of the same save
///     (inside the service's transaction when there is one), so every generated row stays identifiable.
/// The registry also keeps, per table, the highest key that existed before the first seed ("baseline"):
/// removal refuses any key at or below it, as a second guarantee that original rows are never deleted.
/// </summary>
public sealed class TestDataTracker : SaveChangesInterceptor
{
    public const string Table = "TestDataRegistry";

    private readonly List<EntityEntry> _pending = [];
    private readonly HashSet<(string Type, string Key)> _generated = [];
    private bool _writing;

    /// <summary>The seeding stage the rows belong to (a stage that did not finish is redone from scratch).</summary>
    public string Phase { get; set; } = "";
    public int Added { get; private set; }

    public void Remember(IEnumerable<(string Type, string Key)> generated)
    {
        foreach (var g in generated) _generated.Add(g);
    }

    public void Forget(IEnumerable<(string Type, string Key)> removed)
    {
        foreach (var r in removed) _generated.Remove(r);
    }

    // ------------------------------------------------------------------ interception

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context!);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Capture(eventData.Context!);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        RecordAsync(eventData.Context!, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        await RecordAsync(eventData.Context!, ct);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _pending.Clear();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        _pending.Clear();
        return Task.CompletedTask;
    }

    private void Capture(DbContext context)
    {
        _pending.Clear();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    _pending.Add(entry);
                    break;
                case EntityState.Modified or EntityState.Deleted when !_generated.Contains((entry.Metadata.Name, KeyOf(entry))):
                    _pending.Clear();
                    throw new InvalidOperationException(
                        $"Stopped: the seeder tried to {(entry.State == EntityState.Deleted ? "delete" : "change")} an existing {entry.Metadata.ClrType.Name} " +
                        $"(key {KeyOf(entry)}) that is not generated test data. Existing records are never changed; this save was cancelled.");
            }
        }
    }

    private async Task RecordAsync(DbContext context, CancellationToken ct)
    {
        if (_writing || _pending.Count == 0) return;
        var rows = _pending.Select(e => (Type: e.Metadata.Name, Key: KeyOf(e))).Where(r => _generated.Add(r)).ToList();
        _pending.Clear();
        if (rows.Count == 0) return;
        _writing = true;
        try
        {
            await InsertAsync(context.Database, rows.Select(r => (Phase, "A", r.Type, r.Key, (string?)null)).ToList(), ct);
            Added += rows.Count;
        }
        finally { _writing = false; }
    }

    private static string KeyOf(EntityEntry entry) =>
        JsonSerializer.Serialize(entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue));

    // ------------------------------------------------------------------ registry table

    public static async Task EnsureTableAsync(SchoolDbContext db, CancellationToken ct = default) => await db.Database.ExecuteSqlRawAsync($"""
        IF OBJECT_ID(N'dbo.{Table}') IS NULL
        BEGIN
            CREATE TABLE dbo.{Table} (
                Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Kind char(1) NOT NULL,                -- A = generated row, P = stage finished, B = baseline of a table
                Phase nvarchar(40) NOT NULL,
                EntityType nvarchar(200) NOT NULL,
                KeyJson nvarchar(400) NOT NULL,
                Info nvarchar(max) NULL,
                RecordedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
            CREATE UNIQUE INDEX UX_{Table}_Row ON dbo.{Table} (Kind, EntityType, KeyJson);
        END
        """, ct);

    public static async Task<bool> TableExistsAsync(SchoolDbContext db, CancellationToken ct = default) =>
        await db.Database.SqlQueryRaw<int>($"SELECT CASE WHEN OBJECT_ID(N'dbo.{Table}') IS NULL THEN 0 ELSE 1 END AS Value").SingleAsync(ct) == 1;

    public static async Task InsertAsync(DatabaseFacade database, IReadOnlyList<(string Phase, string Kind, string Type, string Key, string? Info)> rows, CancellationToken ct = default)
    {
        foreach (var batch in rows.Chunk(300))
        {
            var values = new List<string>();
            var args = new List<object>();
            foreach (var r in batch)
            {
                var i = args.Count;
                values.Add($"(@p{i}, @p{i + 1}, @p{i + 2}, @p{i + 3}, @p{i + 4})");
                args.Add(new SqlParameter($"@p{i}", SqlDbType.Char, 1) { Value = r.Kind });
                args.Add(new SqlParameter($"@p{i + 1}", r.Phase));
                args.Add(new SqlParameter($"@p{i + 2}", r.Type));
                args.Add(new SqlParameter($"@p{i + 3}", r.Key));
                args.Add(new SqlParameter($"@p{i + 4}", SqlDbType.NVarChar, -1) { Value = (object?)r.Info ?? DBNull.Value });
            }
            // Only parameter names are interpolated; every value is a parameter.
            var sql = $"INSERT INTO dbo.{Table} (Kind, Phase, EntityType, KeyJson, Info) VALUES {string.Join(", ", values)}";
            await database.ExecuteSqlRawAsync(sql, args, ct);
        }
    }

    public sealed class Row
    {
        public string Kind { get; set; } = "";
        public string Phase { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string KeyJson { get; set; } = "";
        public string? Info { get; set; }
    }

    public static async Task<List<Row>> ReadAsync(SchoolDbContext db, CancellationToken ct = default) =>
        await db.Database.SqlQueryRaw<Row>($"SELECT Kind, Phase, EntityType, KeyJson, Info FROM dbo.{Table} ORDER BY Id").ToListAsync(ct);

    // ------------------------------------------------------------------ baseline (original rows)

    /// <summary>
    /// Per table: the highest numeric key (or every text key) that exists before the first seed. Recorded
    /// once; generated rows always get higher keys, and removal refuses anything at or below these values.
    /// </summary>
    public static async Task RecordBaselineAsync(SchoolDbContext db, IReadOnlySet<string>? alreadyRecorded = null, CancellationToken ct = default)
    {
        var rows = new List<(string, string, string, string, string?)>();
        foreach (var (table, column, numeric, _) in Tables(db.Model))
        {
            if (alreadyRecorded?.Contains(table) == true) continue; // a table's baseline is recorded once, before its first generated row
            var sql = numeric
                ? $"SELECT CAST(MAX([{column}]) AS nvarchar(40)) AS Value FROM [{table}]"
                : $"SELECT (SELECT [{column}] AS k FROM [{table}] FOR JSON PATH) AS Value";
            var value = await db.Database.SqlQueryRaw<string?>(sql).SingleAsync(ct);
            rows.Add(("baseline", "B", table, column, value));
        }
        if (rows.Count > 0) await InsertAsync(db.Database, rows, ct);
    }

    /// <summary>Mapped tables with their first key column.</summary>
    public static IEnumerable<(string Table, string Column, bool Numeric, IEntityType Type)> Tables(IModel model) =>
        model.GetEntityTypes().Where(t => t.GetTableName() is not null && t.BaseType is null && t.FindPrimaryKey() is not null)
            .Select(t =>
            {
                var key = t.FindPrimaryKey()!.Properties[0];
                var column = key.GetColumnName(StoreObjectIdentifier.Table(t.GetTableName()!, t.GetSchema()))!;
                var numeric = (Nullable.GetUnderlyingType(key.ClrType) ?? key.ClrType) is var k && (k == typeof(int) || k == typeof(long) || k == typeof(short));
                return (t.GetTableName()!, column, numeric, t);
            });

    /// <summary>Tables in delete order: every table before the tables it references.</summary>
    public static List<string> DeleteOrder(IModel model)
    {
        var tables = model.GetEntityTypes().Where(t => t.GetTableName() is not null)
            .GroupBy(t => t.GetTableName()!).ToDictionary(g => g.Key, g => g.ToList());
        var references = tables.ToDictionary(t => t.Key, t => t.Value
            .SelectMany(e => e.GetForeignKeys()).Select(fk => fk.PrincipalEntityType.GetTableName()!)
            .Where(p => p != t.Key).ToHashSet());
        var order = new List<string>();
        var left = tables.Keys.ToHashSet();
        while (left.Count > 0)
        {
            var next = left.Where(t => !left.Any(o => o != t && references[o].Contains(t))).OrderBy(t => t).ToList();
            if (next.Count == 0) throw new InvalidOperationException("Circular references between tables: " + string.Join(", ", left));
            order.AddRange(next);
            left.ExceptWith(next);
        }
        return order;
    }

    // ------------------------------------------------------------------ safe removal

    /// <summary>
    /// Deletes the given generated rows (all of them, or one unfinished stage). Before deleting anything it
    /// checks that (1) every key is above the baseline of original rows and (2) no row that is not generated
    /// refers to a generated row (it would otherwise be deleted by a cascade or block the delete).
    /// Throws without deleting when a check fails. Call inside a transaction.
    /// </summary>
    public static async Task<int> DeleteGeneratedAsync(SchoolDbContext db, IReadOnlyList<Row> generated, IReadOnlyList<Row> baseline, CancellationToken ct = default)
    {
        var model = db.Model;
        var byTable = generated
            .Select(r => (Type: model.FindEntityType(r.EntityType), Key: JsonSerializer.Deserialize<JsonElement[]>(r.KeyJson)!))
            .Where(x => x.Type is not null)
            .GroupBy(x => x.Type!.GetTableName()!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var keyText = byTable.ToDictionary(t => t.Key, t => t.Value.Select(x => string.Join("|", x.Key.Select(Text))).ToHashSet());

        // 1) Never at or below the baseline of original rows.
        foreach (var (table, rows) in byTable)
        {
            var b = baseline.FirstOrDefault(x => x.EntityType == table);
            if (b?.Info is null) continue;
            var original = rows.Where(r => r.Key[0].ValueKind == JsonValueKind.Number
                ? long.TryParse(b.Info, out var max) && r.Key[0].GetInt64() <= max
                : b.Info.Contains(JsonSerializer.Serialize(r.Key[0].GetString()), StringComparison.Ordinal)).ToList();
            if (original.Count > 0)
                throw new InvalidOperationException($"Refused: {original.Count} row(s) of {table} are listed as generated but existed before seeding. Nothing was removed.");
        }

        // 2) No other (non-generated) row may refer to a generated row — except derived records, removed with it below.
        foreach (var dependent in model.GetEntityTypes().Where(t => t.GetTableName() is not null && !Derived.Contains(t.ClrType)))
        {
            foreach (var fk in dependent.GetForeignKeys().Where(f => f.PrincipalKey.IsPrimaryKey()))
            {
                var principalTable = fk.PrincipalEntityType.GetTableName()!;
                if (!keyText.TryGetValue(principalTable, out var principalKeys)) continue;
                var table = dependent.GetTableName()!;
                var store = StoreObjectIdentifier.Table(table, dependent.GetSchema());
                var pk = dependent.FindPrimaryKey()!.Properties.Select(p => $"[{p.GetColumnName(store)}]");
                var fkColumns = fk.Properties.Select(p => $"[{p.GetColumnName(store)}]").ToList();
                var sql = $"SELECT CONCAT_WS('|', {string.Join(", ", pk.Select(c => $"CAST({c} AS nvarchar(100))").Append("''").Take(Math.Max(2, pk.Count())))}) AS K, " +
                          $"CONCAT_WS('|', {string.Join(", ", fkColumns.Select(c => $"CAST({c} AS nvarchar(100))").Append("''").Take(Math.Max(2, fkColumns.Count)))}) AS F " +
                          $"FROM [{table}] WHERE {string.Join(" AND ", fkColumns.Select(c => c + " IS NOT NULL"))}";
                var own = keyText.GetValueOrDefault(table) ?? [];
                var refs = await QueryPairsAsync(db, sql, ct);
                var foreign = refs.Count(r => principalKeys.Contains(r.F.TrimEnd('|')) && !own.Contains(r.K.TrimEnd('|')));
                if (foreign > 0)
                    throw new InvalidOperationException($"Refused: {foreign} record(s) in {table} that were not generated refer to generated {principalTable}. " +
                        "Delete or change those records first. Nothing was removed.");
            }
        }

        // 3) Derived records (alerts and their events, incident audit history) that point at generated rows go with
        //    them, also when the application created them after seeding (the alert sweep, a follow-up note, a PDF export).
        var deleted = 0;
        foreach (var dependent in model.GetEntityTypes().Where(t => Derived.Contains(t.ClrType)))
        {
            var table = dependent.GetTableName()!;
            var store = StoreObjectIdentifier.Table(table, dependent.GetSchema());
            foreach (var fk in dependent.GetForeignKeys().Where(f => f.Properties.Count == 1 && f.PrincipalKey.IsPrimaryKey()))
            {
                if (!byTable.TryGetValue(fk.PrincipalEntityType.GetTableName()!, out var principals)) continue;
                var column = fk.Properties[0].GetColumnName(store)!;
                foreach (var batch in principals.Where(p => p.Key[0].ValueKind == JsonValueKind.Number).Select(p => p.Key[0].GetInt64()).Chunk(1000))
                {
                    var args = batch.Select((k, i) => (object)new SqlParameter($"@p{i}", k)).ToList();
                    var sql = $"DELETE FROM [{dependent.GetSchema() ?? "dbo"}].[{table}] WHERE [{column}] IN ({string.Join(", ", batch.Select((_, i) => $"@p{i}"))})";
                    deleted += await db.Database.ExecuteSqlRawAsync(sql, args, ct);
                }
            }
        }

        // 4) Delete, every table before the tables it references.
        foreach (var table in DeleteOrder(model).Where(byTable.ContainsKey))
        {
            var type = byTable[table][0].Type!;
            var store = StoreObjectIdentifier.Table(table, type.GetSchema());
            var columns = type.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName(store)!).ToList();
            foreach (var batch in byTable[table].Chunk(columns.Count == 1 ? 1000 : 300))
            {
                var args = new List<object>();
                string Param(JsonElement e)
                {
                    args.Add(new SqlParameter($"@p{args.Count}", e.ValueKind == JsonValueKind.Number ? e.GetInt64() : e.GetString()!));
                    return $"@p{args.Count - 1}";
                }
                var where = columns.Count == 1
                    ? $"[{columns[0]}] IN ({string.Join(", ", batch.Select(b => Param(b.Key[0])))})"
                    : string.Join(" OR ", batch.Select(b => "(" + string.Join(" AND ", columns.Select((c, i) => $"[{c}] = {Param(b.Key[i])}")) + ")"));
                // Table and column names come from the EF model; key values are parameters.
                var sql = $"DELETE FROM [{type.GetSchema() ?? "dbo"}].[{table}] WHERE {where}";
                deleted += await db.Database.ExecuteSqlRawAsync(sql, args, ct);
            }
        }
        return deleted;
    }

    /// <summary>Records computed from other records: removed together with the generated rows they refer to.</summary>
    private static readonly Type[] Derived = [typeof(AttendanceAlert), typeof(AttendanceAlertEvent), typeof(IncidentHistory)];

    private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64().ToString() : e.GetString() ?? "";

    private static async Task<List<(string K, string F)>> QueryPairsAsync(SchoolDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var result = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }
}
