using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AuditProposedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "proposed_by",
                table: "audit_entries",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "User");

            // Rows written before this column existed recorded agent provenance in actor_type; move it.
            migrationBuilder.Sql("UPDATE audit_entries SET proposed_by = actor_type, actor_type = 'User' WHERE actor_type = 'Agent';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "proposed_by",
                table: "audit_entries");
        }
    }
}
