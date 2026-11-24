namespace BusinessLogicLayer.Models;

public sealed record ScheduleQuery(
    DateTime From,
    DateTime To,
    long? RoomId = null,
    int Limit = 20,
    string? ContinuationToken = null);

public sealed record ScheduleItemResult(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    RoomResult Room,
    int Capacity,
    int FreeSeats,
    int MySeats);

public sealed record ScheduleResult(IReadOnlyList<ScheduleItemResult> Items, string? ContinuationToken);

public sealed record LoadQuery(DateOnly From, DateOnly To, long? RoomId = null);

public sealed record RoomLoadResult(
    DateOnly Date,
    RoomResult Room,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);

public sealed record LoadResult(IReadOnlyList<RoomLoadResult> Items);
