using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class IdempotencyRequestConfiguration : IEntityTypeConfiguration<IdempotencyRequest>
{
    public void Configure(EntityTypeBuilder<IdempotencyRequest> builder)
    {
        builder.ToTable("idempotency_requests", table =>
            table.HasCheckConstraint("CK_idempotency_requests_key_hash", "octet_length(key_hash) = 32"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Operation).HasColumnName("operation").IsRequired();
        builder.Property(x => x.OriginalKey).HasColumnName("original_key").IsRequired();
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").IsRequired();
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.EnrollmentId).HasColumnName("enrollment_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

        // решение: индексируем SHA-256, чтобы длина клиентского ключа не ограничивалась размером B-tree записи.
        builder.HasIndex(x => new { x.UserId, x.Operation, x.KeyHash }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasOne<Enrollment>().WithMany().HasForeignKey(x => x.EnrollmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
