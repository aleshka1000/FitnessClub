namespace DataAccessLayer.Models;

public sealed record ScheduleClassData(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    long RoomId,
    string RoomName,
    int Capacity,
    long BookedSeats,
    long MySeats);

public sealed record RoomLoadData(
    DateOnly Date,
    long RoomId,
    string RoomName,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);
