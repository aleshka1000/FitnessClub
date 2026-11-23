namespace BusinessLogicLayer.Models;

public sealed record WaitlistJoinResult(long Id, long ClassId, int Seats, int Position, DateTime CreatedAt);
