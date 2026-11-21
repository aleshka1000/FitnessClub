namespace Common.Database.UnitOfWork;

public sealed class ConcurrencyRetriesExhaustedException(Exception innerException)
    : Exception("The operation could not complete after five concurrency attempts.", innerException);
