using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeExclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_exclusion",
                table: "knowledge_chunks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Chunks indexed before this migration: same rule as Chunker.IsExclusionHeading, so no reindex is needed.
            migrationBuilder.Sql("UPDATE knowledge_chunks SET is_exclusion = 1 WHERE lower(section) LIKE '%does not cover%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_exclusion",
                table: "knowledge_chunks");
        }
    }
}
