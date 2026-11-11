using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SchoolManagement.Web.Data;

public static class DbErrors
{
    /// <summary>SQL Server 2601 / 2627: a unique index or constraint rejected the row.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
