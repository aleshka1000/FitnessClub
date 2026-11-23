using DataAccessLayer.Models;

namespace BusinessLogicLayer.Models;

public sealed record RoomResult(long Id, string Name);

public sealed record EnrollmentClassResult(long Id, string Title, DateTime StartsAt, int DurationMinutes,
    bool IsCancelled, RoomResult Room);

public sealed record EnrollmentPassResult(int RemainingVisits, DateOnly ValidUntil);

public sealed record EnrollmentDetailsResult(long Id, string Status, int Seats, DateTime CreatedAt,
    EnrollmentClassResult Class, EnrollmentPassResult Pass)
{
    public static EnrollmentDetailsResult From(EnrollmentDetailsData data) => new(data.Id, data.Status,
        data.Seats, data.CreatedAt, new EnrollmentClassResult(data.ClassId, data.Title, data.StartsAt,
            data.DurationMinutes, data.IsCancelled, new RoomResult(data.RoomId, data.RoomName)),
        new EnrollmentPassResult(data.RemainingVisits, data.ValidUntil));
}
