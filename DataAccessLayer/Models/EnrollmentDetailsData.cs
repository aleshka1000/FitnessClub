namespace DataAccessLayer.Models;

public sealed record EnrollmentDetailsData(long Id, string Status, int Seats, DateTime CreatedAt,
    long ClassId, string Title, DateTime StartsAt, int DurationMinutes, bool IsCancelled,
    long RoomId, string RoomName, int RemainingVisits, DateOnly ValidUntil,
    uint EnrollmentVersion, uint PassVersion);
