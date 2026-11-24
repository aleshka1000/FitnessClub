using WebApi.Models.Enrollments;

namespace WebApi.Models;

public sealed record ScheduleItemResponse(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    RoomResponse Room,
    int Capacity,
    int FreeSeats,
    int MySeats);

public sealed record ScheduleResponse(IReadOnlyList<ScheduleItemResponse> Items, string? ContinuationToken);

public sealed record RoomLoadResponse(
    DateOnly Date,
    RoomResponse Room,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);

public sealed record LoadResponse(IReadOnlyList<RoomLoadResponse> Items);
