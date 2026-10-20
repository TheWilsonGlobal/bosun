using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentAndEvals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    phase = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    created_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tool_calls = table.Column<int>(type: "INTEGER", nullable: false),
                    max_tool_calls = table.Column<int>(type: "INTEGER", nullable: false),
                    elapsed_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    max_total_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    prompt_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    candidate_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    thought_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    total_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    change_set_id = table.Column<int>(type: "INTEGER", nullable: true),
                    result_kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    result_message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    error_message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    suite = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    runs_per_case = table.Column<int>(type: "INTEGER", nullable: false),
                    cases = table.Column<int>(type: "INTEGER", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    passed = table.Column<int>(type: "INTEGER", nullable: false),
                    pass_rate = table.Column<double>(type: "REAL", nullable: false),
                    prompt_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    candidate_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    thought_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<double>(type: "REAL", nullable: false),
                    results_file = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_steps",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    run_id = table.Column<int>(type: "INTEGER", nullable: false),
                    seq = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_steps_agent_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_results",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    eval_run_id = table.Column<int>(type: "INTEGER", nullable: false),
                    case_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    agent_status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    tool_calls = table.Column<int>(type: "INTEGER", nullable: false),
                    tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    agent_run_id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_eval_results_eval_runs_eval_run_id",
                        column: x => x.eval_run_id,
                        principalTable: "eval_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_sets_agent_run_id",
                table: "change_sets",
                column: "agent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_project_status",
                table: "agent_runs",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_agent_steps_run_seq",
                table: "agent_steps",
                columns: new[] { "run_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_run_case",
                table: "eval_results",
                columns: new[] { "eval_run_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_suite_started",
                table: "eval_runs",
                columns: new[] { "suite", "started_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_change_sets_agent_runs_agent_run_id",
                table: "change_sets",
                column: "agent_run_id",
                principalTable: "agent_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_change_sets_agent_runs_agent_run_id",
                table: "change_sets");

            migrationBuilder.DropTable(
                name: "agent_steps");

            migrationBuilder.DropTable(
                name: "eval_results");

            migrationBuilder.DropTable(
                name: "agent_runs");

            migrationBuilder.DropTable(
                name: "eval_runs");

            migrationBuilder.DropIndex(
                name: "IX_change_sets_agent_run_id",
                table: "change_sets");
        }
    }
}
