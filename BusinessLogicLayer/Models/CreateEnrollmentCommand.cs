namespace BusinessLogicLayer.Models;

public sealed record CreateEnrollmentCommand(long ClassId, int Seats, string IdempotencyKey);
