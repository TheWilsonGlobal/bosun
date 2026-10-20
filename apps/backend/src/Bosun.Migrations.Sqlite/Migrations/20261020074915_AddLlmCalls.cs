using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_calls",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    feature = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    prompt_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    candidate_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    thought_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    total_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    finish_reason = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    cost_usd = table.Column<double>(type: "REAL", nullable: false),
                    agent_run_id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_calls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_feature_model",
                table: "llm_calls",
                columns: new[] { "feature", "model" });

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_timestamp",
                table: "llm_calls",
                column: "timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_calls");
        }
    }
}
