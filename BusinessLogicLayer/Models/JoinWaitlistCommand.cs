namespace BusinessLogicLayer.Models;

public sealed record JoinWaitlistCommand(long ClassId, int Seats, string IdempotencyKey);
