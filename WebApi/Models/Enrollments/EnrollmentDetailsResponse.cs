using BusinessLogicLayer.Models;

namespace WebApi.Models.Enrollments;

public sealed record RoomResponse(long Id, string Name);

public sealed record EnrollmentClassResponse(
    long Id,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    bool IsCancelled,
    RoomResponse Room);

public sealed record EnrollmentPassResponse(int RemainingVisits, DateOnly ValidUntil);

public sealed record EnrollmentDetailsResponse(
    long Id,
    string Status,
    int Seats,
    DateTime CreatedAt,
    EnrollmentClassResponse Class,
    EnrollmentPassResponse Pass)
{
    public static EnrollmentDetailsResponse From(EnrollmentDetailsResult value) =>
        new(
            value.Id,
            value.Status,
            value.Seats,
            value.CreatedAt,
            new EnrollmentClassResponse(
                value.Class.Id,
                value.Class.Title,
                value.Class.StartsAt,
                value.Class.DurationMinutes,
                value.Class.IsCancelled,
                new RoomResponse(value.Class.Room.Id, value.Class.Room.Name)),
            new EnrollmentPassResponse(value.Pass.RemainingVisits, value.Pass.ValidUntil));
}
