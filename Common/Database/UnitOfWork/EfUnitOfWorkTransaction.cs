using Microsoft.EntityFrameworkCore.Storage;

namespace Common.Database.UnitOfWork;

internal sealed class EfUnitOfWorkTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
{
    public Task CommitAsync(CancellationToken ct = default) => transaction.CommitAsync(ct);

    public Task RollbackAsync(CancellationToken ct = default) => transaction.RollbackAsync(ct);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
