using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WebApi.Tests;

internal sealed class SqlCommandRecorder : DbCommandInterceptor
{
    public ConcurrentQueue<string> Commands { get; } = new();

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            Commands.Enqueue(command.CommandText);

        return ValueTask.FromResult(result);
    }
}
