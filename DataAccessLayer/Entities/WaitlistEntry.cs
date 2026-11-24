using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class WaitlistEntry : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public string Status { get; set; } = "waiting";

    public DateTime CreatedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? SkipReason { get; set; }

    public long? PromotedEnrollmentId { get; set; }

    public Enrollment? PromotedEnrollment { get; set; }

    public uint Version { get; set; }
}
