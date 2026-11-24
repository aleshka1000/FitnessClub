using Common.Database.UnitOfWork;
using DataAccessLayer.Configurations;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Npgsql;

namespace DataAccessLayer.UnitOfWork;

public interface IUnitOfWork : IUnitOfWorkBase
{
    IRoomRepository Room { get; }

    IClassRepository Class { get; }

    IEnrollmentRepository Enrollment { get; }

    IPassRepository Pass { get; }

    IIdempotencyRepository Idempotency { get; }

    IWaitlistRepository Waitlist { get; }
}

public class UnitOfWork : UnitOfWorkBase, IUnitOfWork
{
    private IIdempotencyRepository? _idempotency;

    public UnitOfWork(ClubDbContext dbContext)
        : base(dbContext)
    {
    }

    public IRoomRepository Room => GetRepository<Room, IRoomRepository>(set => new RoomRepository(set));

    public IClassRepository Class =>
        GetRepository<Class, IClassRepository>(set => new ClassRepository(set, DbContext.Set<Enrollment>()));

    public IEnrollmentRepository Enrollment =>
        GetRepository<Enrollment, IEnrollmentRepository>(set => new EnrollmentRepository(set, DbContext.Set<Pass>()));

    public IPassRepository Pass => GetRepository<Pass, IPassRepository>(set => new PassRepository(set));

    public IIdempotencyRepository Idempotency => _idempotency ??= new IdempotencyRepository((ClubDbContext)DbContext);

    public IWaitlistRepository Waitlist =>
        GetRepository<WaitlistEntry, IWaitlistRepository>(set => new WaitlistRepository(set));

    protected override bool IsConcurrencyConflict(Exception exception)
    {
        if (base.IsConcurrencyConflict(exception))
        {
            return true;
        }

        // решение: повторяем deadlock и гонку единственного ожидания, остальные ограничения не маскируем.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState == PostgresErrorCodes.DeadlockDetected
                    || postgres.SqlState == PostgresErrorCodes.UniqueViolation
                    && postgres.ConstraintName == WaitlistEntryConfiguration.WaitingUserClassIndex;
            }
        }
        return false;
    }
}
