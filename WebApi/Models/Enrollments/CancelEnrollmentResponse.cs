namespace WebApi.Models.Enrollments;

public sealed record CancelEnrollmentResponse(long Id, string Status, int RefundedVisits);
