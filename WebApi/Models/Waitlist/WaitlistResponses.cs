namespace WebApi.Models.Waitlist;

public sealed record JoinWaitlistResponse(long Id, long ClassId, int Seats, int Position, DateTime CreatedAt);

public sealed record CancelWaitlistResponse(long Id, string Status);
