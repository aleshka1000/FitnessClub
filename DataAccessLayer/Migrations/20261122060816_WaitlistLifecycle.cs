using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class WaitlistLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "waitlist_entry_id",
                table: "idempotency_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    skip_reason = table.Column<string>(type: "text", nullable: true),
                    promoted_enrollment_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waitlist_entries", x => x.id);
                    table.CheckConstraint("CK_waitlist_entries_seats", "seats >= 1");
                    table.ForeignKey(
                        name: "FK_waitlist_entries_classes_class_id",
                        column: x => x.class_id,
                        principalTable: "classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_enrollments_promoted_enrollment_id",
                        column: x => x.promoted_enrollment_id,
                        principalTable: "enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_waitlist_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_requests_waitlist_entry_id",
                table: "idempotency_requests",
                column: "waitlist_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_class_id_created_at_id",
                table: "waitlist_entries",
                columns: new[] { "class_id", "created_at", "id" },
                filter: "status = 'waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_waitlist_entries_promoted_enrollment_id",
                table: "waitlist_entries",
                column: "promoted_enrollment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_waitlist_waiting_user_class",
                table: "waitlist_entries",
                columns: new[] { "user_id", "class_id" },
                unique: true,
                filter: "status = 'waiting'");

            migrationBuilder.AddForeignKey(
                name: "FK_idempotency_requests_waitlist_entries_waitlist_entry_id",
                table: "idempotency_requests",
                column: "waitlist_entry_id",
                principalTable: "waitlist_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_idempotency_requests_waitlist_entries_waitlist_entry_id",
                table: "idempotency_requests");

            migrationBuilder.DropTable(
                name: "waitlist_entries");

            migrationBuilder.DropIndex(
                name: "IX_idempotency_requests_waitlist_entry_id",
                table: "idempotency_requests");

            migrationBuilder.DropColumn(
                name: "waitlist_entry_id",
                table: "idempotency_requests");

        }
    }
}
