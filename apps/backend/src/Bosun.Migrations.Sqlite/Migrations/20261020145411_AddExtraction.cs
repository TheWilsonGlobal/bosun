using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_documents",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    stored_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    page_count = table.Column<int>(type: "INTEGER", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    uploaded_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_documents_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extractions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    source_document_id = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    phase = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    pages_total = table.Column<int>(type: "INTEGER", nullable: false),
                    pages_done = table.Column<int>(type: "INTEGER", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    pages_json = table.Column<string>(type: "TEXT", nullable: false),
                    llm_calls = table.Column<int>(type: "INTEGER", nullable: false),
                    prompt_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    candidate_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    thought_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    total_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<double>(type: "REAL", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extractions", x => x.id);
                    table.ForeignKey(
                        name: "FK_extractions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_extractions_source_documents_source_document_id",
                        column: x => x.source_document_id,
                        principalTable: "source_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extracted_rows",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    extraction_id = table.Column<int>(type: "INTEGER", nullable: false),
                    seq = table.Column<int>(type: "INTEGER", nullable: false),
                    page = table.Column<int>(type: "INTEGER", nullable: false),
                    source_path = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    source_ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    source_snippet = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    bbox_json = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    values_json = table.Column<string>(type: "TEXT", nullable: false),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false),
                    fields_json = table.Column<string>(type: "TEXT", nullable: false),
                    flags_json = table.Column<string>(type: "TEXT", nullable: false),
                    violations_json = table.Column<string>(type: "TEXT", nullable: false),
                    score = table.Column<double>(type: "REAL", nullable: false),
                    band = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    human_corrected = table.Column<bool>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    change_set_id = table.Column<int>(type: "INTEGER", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extracted_rows", x => x.id);
                    table.ForeignKey(
                        name: "FK_extracted_rows_extractions_extraction_id",
                        column: x => x.extraction_id,
                        principalTable: "extractions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extracted_rows_extraction_band",
                table: "extracted_rows",
                columns: new[] { "extraction_id", "band" });

            migrationBuilder.CreateIndex(
                name: "ux_extracted_rows_extraction_seq",
                table: "extracted_rows",
                columns: new[] { "extraction_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_extractions_source_document_id",
                table: "extractions",
                column: "source_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_extractions_project_status",
                table: "extractions",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_source_documents_project",
                table: "source_documents",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_source_documents_stored_name",
                table: "source_documents",
                column: "stored_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extracted_rows");

            migrationBuilder.DropTable(
                name: "extractions");

            migrationBuilder.DropTable(
                name: "source_documents");
        }
    }
}
