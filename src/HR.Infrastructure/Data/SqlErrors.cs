using Microsoft.Data.SqlClient;

namespace HR.Infrastructure.Data;

/// <summary>Recognises SQL Server errors in an exception chain (EF wraps them in DbUpdateException).</summary>
public static class SqlErrors
{
    public static bool IsDeadlock(Exception exception) => Has(exception, 1205);

    public static bool IsUniqueViolation(Exception exception) => Has(exception, 2601) || Has(exception, 2627);

    private static bool Has(Exception? exception, int number)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && sql.Errors.Cast<SqlError>().Any(x => x.Number == number))
            {
                return true;
            }
        }

        return false;
    }
}
