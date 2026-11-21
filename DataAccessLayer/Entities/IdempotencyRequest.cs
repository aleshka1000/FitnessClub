using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class IdempotencyRequest : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string Operation { get; set; } = null!;

    public string OriginalKey { get; set; } = null!;

    public byte[] KeyHash { get; set; } = null!;

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public long? EnrollmentId { get; set; }

    public DateTime CreatedAt { get; set; }
}
