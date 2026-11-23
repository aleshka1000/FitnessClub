using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public const string WaitingUserClassIndex = "UX_waitlist_waiting_user_class";

    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries", table =>
            table.HasCheckConstraint("CK_waitlist_entries_seats", "seats >= 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.SkipReason).HasColumnName("skip_reason");
        builder.Property(x => x.PromotedEnrollmentId).HasColumnName("promoted_enrollment_id");
        builder.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasOne<Class>().WithMany().HasForeignKey(x => x.ClassId);
        builder.HasOne(x => x.PromotedEnrollment).WithMany().HasForeignKey(x => x.PromotedEnrollmentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.UserId, x.ClassId }).IsUnique()
            .HasFilter("status = 'waiting'").HasDatabaseName(WaitingUserClassIndex);
        builder.HasIndex(x => new { x.ClassId, x.CreatedAt, x.Id }).HasFilter("status = 'waiting'");
        builder.HasIndex(x => x.PromotedEnrollmentId).IsUnique();
    }
}
