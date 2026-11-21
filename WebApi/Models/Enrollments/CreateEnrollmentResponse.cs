namespace WebApi.Models.Enrollments;

public sealed record CreateEnrollmentResponse(long Id, long ClassId, int Seats, string Status, DateTime CreatedAt);
