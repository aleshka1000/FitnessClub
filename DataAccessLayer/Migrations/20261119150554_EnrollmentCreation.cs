using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "passes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "classes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "idempotency_requests",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    original_key = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    enrollment_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_requests", x => x.id);
                    table.CheckConstraint("CK_idempotency_requests_key_hash", "octet_length(key_hash) = 32");
                    table.ForeignKey(
                        name: "FK_idempotency_requests_enrollments_enrollment_id",
                        column: x => x.enrollment_id,
                        principalTable: "enrollments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_idempotency_requests_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_passes_remaining_visits",
                table: "passes",
                sql: "remaining_visits >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_enrollments_seats",
                table: "enrollments",
                sql: "seats >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_requests_enrollment_id",
                table: "idempotency_requests",
                column: "enrollment_id");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_requests_user_id_operation_key_hash",
                table: "idempotency_requests",
                columns: new[] { "user_id", "operation", "key_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_requests");

            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_passes_remaining_visits",
                table: "passes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_enrollments_seats",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "version",
                table: "passes");

            migrationBuilder.DropColumn(
                name: "version",
                table: "classes");

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id");
        }
    }
}
