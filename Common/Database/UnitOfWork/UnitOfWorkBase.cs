using Common.Database.Interfaces;
using Common.Database.Repositories.Generic;
using Microsoft.EntityFrameworkCore;

namespace Common.Database.UnitOfWork;

public interface IUnitOfWorkBase
{
    DbContext DbContext { get; }

    IGenericRepository<T> GetRepository<T>() where T : class, IEntity;

    Task SaveChangesAsync(CancellationToken ct = default);

    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default);

    Task<T> ExecuteWithConcurrencyRetryAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default);
}

public abstract class UnitOfWorkBase : IUnitOfWorkBase
{
    private readonly Dictionary<Type, object> _repositories;

    protected UnitOfWorkBase(DbContext dbContext)
    {
        DbContext = dbContext;
        _repositories = new Dictionary<Type, object>();
    }

    public DbContext DbContext { get; }

    public IGenericRepository<T> GetRepository<T>() where T : class, IEntity
    {
        var type = typeof(T);

        if (!_repositories.TryGetValue(type, out var repository))
        {
            repository = new GenericRepository<T>(DbContext.Set<T>());
            _repositories[type] = repository;
        }

        return (IGenericRepository<T>)repository;
    }

    protected TRepository GetRepository<TEntity, TRepository>(Func<DbSet<TEntity>, TRepository> factory)
        where TEntity : class, IEntity
        where TRepository : class
    {
        var type = typeof(TRepository);

        if (!_repositories.TryGetValue(type, out var repository))
        {
            repository = factory(DbContext.Set<TEntity>());
            _repositories[type] = repository;
        }

        return (TRepository)repository;
    }

    public virtual async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await DbContext.SaveChangesAsync(ct);
    }

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        var transaction = await DbContext.Database.BeginTransactionAsync(ct);
        return new EfUnitOfWorkTransaction(transaction);
    }

    public async Task<T> ExecuteWithConcurrencyRetryAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        // решение: повторяем всю операцию до пяти раз; затяжной конфликт отдаём как временную недоступность.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation(ct);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                DbContext.ChangeTracker.Clear();

                if (attempt == 4)
                {
                    throw new ConcurrencyRetriesExhaustedException(exception);
                }

                await Task.Delay(Random.Shared.Next(20, 51), ct);
            }
        }

        throw new InvalidOperationException("Unreachable concurrency retry state.");
    }
}
