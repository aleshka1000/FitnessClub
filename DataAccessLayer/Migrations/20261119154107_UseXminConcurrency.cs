using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class UseXminConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // решение: xmin уже существует в PostgreSQL; удаляем только прежние прикладные счётчики.
            migrationBuilder.DropColumn(
                name: "version",
                table: "passes");

            migrationBuilder.DropColumn(
                name: "version",
                table: "classes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
