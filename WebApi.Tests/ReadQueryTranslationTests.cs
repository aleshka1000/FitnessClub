using System.Data.Common;
using DataAccessLayer;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WebApi.Tests;

[TestClass]
public class ReadQueryTranslationTests
{
    [TestMethod]
    public async Task Card_TranslatesToSingleQuery_WithOwnerAndCancelledClass()
    {
        var sql = await CaptureAsync(db => new EnrollmentRepository(db.Enrollments, db.Passes)
            .GetDetailsAsync(123, 456));
        StringAssert.Contains(sql, "JOIN passes");
        StringAssert.Contains(sql, "JOIN classes");
        StringAssert.Contains(sql, "JOIN rooms");
        StringAssert.Contains(sql, "user_id");
        Assert.IsFalse(sql.Contains("NOT (", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Schedule_TranslatesCursorAndSums_WithServerSideLimit()
    {
        var start = new DateTime(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var sql = await CaptureAsync(db => new ClassRepository(db.Classes, db.Enrollments)
            .GetScheduleAsync(456, start, start.AddDays(1), 12, start, 123, 21));
        StringAssert.Contains(sql, "LIMIT");
        StringAssert.Contains(sql, "ORDER BY");
        StringAssert.Contains(sql, "sum(");
        StringAssert.Contains(sql, "user_id");
        StringAssert.Contains(sql, "is_cancelled");
    }

    [TestMethod]
    public async Task Load_TranslatesGroupingAndTotals_UsingUtcDate()
    {
        var start = new DateTime(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var sql = await CaptureAsync(db => new ClassRepository(db.Classes, db.Enrollments)
            .GetLoadAsync(start, start.AddDays(2), 12));
        StringAssert.Contains(sql, "GROUP BY");
        StringAssert.Contains(sql, "LEFT JOIN");
        StringAssert.Contains(sql, "UTC");
        StringAssert.Contains(sql, "ORDER BY");
    }

    private static async Task<string> CaptureAsync(Func<ClubDbContext, Task> query)
    {
        var capture = new CaptureCommand();
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .AddInterceptors(new SuppressConnection(), capture).Options;
        await using var db = new ClubDbContext(options);
        try
        {
            await query(db);
            Assert.Fail("The query must be intercepted before execution.");
        }
        catch (QueryCapturedException)
        {
        }
        Assert.IsNotNull(capture.Sql);
        return capture.Sql;
    }

    private sealed class QueryCapturedException : Exception;

    private sealed class SuppressConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class CaptureCommand : DbCommandInterceptor
    {
        public string? Sql { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Sql = command.CommandText;
            throw new QueryCapturedException();
        }
    }
}
