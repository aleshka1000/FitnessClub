namespace BusinessLogicLayer.Models;

public sealed record EnrollmentCreationResult(long Id, long ClassId, int Seats, string Status, DateTime CreatedAt);
