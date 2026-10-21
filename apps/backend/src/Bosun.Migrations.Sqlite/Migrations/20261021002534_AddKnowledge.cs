using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_chunks",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    section = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    page = table.Column<int>(type: "INTEGER", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    text = table.Column<string>(type: "TEXT", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    embedding = table.Column<byte[]>(type: "BLOB", nullable: true),
                    embedding_model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    indexed_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunks", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledge_chunks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_chunks_project_hash",
                table: "knowledge_chunks",
                columns: new[] { "project_id", "content_hash" });

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_chunks_project_source_ordinal",
                table: "knowledge_chunks",
                columns: new[] { "project_id", "source", "ordinal" },
                unique: true);

            // ---- FTS5 keyword index over chunk text + section title (plan s6.4 / s5.1 "Full-text for RAG") ----------
            // External-content table: only the index lives in knowledge_fts, the text stays in knowledge_chunks. The porter
            // stemmer over unicode61 makes "delays" match "delay"; ranking is bm25(). Only NEW tables are created here, so the
            // tag_fts triggers of the first migration are untouched (EF rebuilds a table only when it ALTERs it).
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE knowledge_fts USING fts5(text, section, content='knowledge_chunks', content_rowid='id', tokenize='porter unicode61 remove_diacritics 2');");
            migrationBuilder.Sql(
                @"CREATE TRIGGER knowledge_fts_ai AFTER INSERT ON knowledge_chunks BEGIN
                    INSERT INTO knowledge_fts(rowid, text, section) VALUES (new.id, new.text, new.section);
                END;");
            migrationBuilder.Sql(
                @"CREATE TRIGGER knowledge_fts_ad AFTER DELETE ON knowledge_chunks BEGIN
                    INSERT INTO knowledge_fts(knowledge_fts, rowid, text, section) VALUES ('delete', old.id, old.text, old.section);
                END;");
            migrationBuilder.Sql(
                @"CREATE TRIGGER knowledge_fts_au AFTER UPDATE OF text, section ON knowledge_chunks BEGIN
                    INSERT INTO knowledge_fts(knowledge_fts, rowid, text, section) VALUES ('delete', old.id, old.text, old.section);
                    INSERT INTO knowledge_fts(rowid, text, section) VALUES (new.id, new.text, new.section);
                END;");
            migrationBuilder.Sql("INSERT INTO knowledge_fts(knowledge_fts) VALUES ('rebuild');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS knowledge_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS knowledge_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS knowledge_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS knowledge_fts;");
            migrationBuilder.DropTable(
                name: "knowledge_chunks");
        }
    }
}
