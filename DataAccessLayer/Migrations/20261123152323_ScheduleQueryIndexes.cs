using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes",
                columns: new[] { "room_id", "starts_at", "id" },
                filter: "NOT is_cancelled");

            migrationBuilder.CreateIndex(
                name: "IX_classes_starts_at_id",
                table: "classes",
                columns: new[] { "starts_at", "id" },
                filter: "NOT is_cancelled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes");

            migrationBuilder.DropIndex(
                name: "IX_classes_starts_at_id",
                table: "classes");

        }
    }
}
