using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only NEW tables and indexes: no existing table is altered or rebuilt, so the hand-written FTS5 triggers on
            // tags / knowledge_chunks (which a SQLite table rebuild would silently drop) are untouched.
            migrationBuilder.CreateTable(
                name: "alarms",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    tag_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    level = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    limit_value = table.Column<double>(type: "REAL", nullable: false),
                    delay_s = table.Column<double>(type: "REAL", nullable: false),
                    hysteresis = table.Column<double>(type: "REAL", nullable: false),
                    unit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    raised_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    raised_value = table.Column<double>(type: "REAL", nullable: false),
                    peak_value = table.Column<double>(type: "REAL", nullable: false),
                    acknowledged_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    acknowledged_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    returned_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    returned_value = table.Column<double>(type: "REAL", nullable: true),
                    cleared_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarms", x => x.id);
                    table.ForeignKey(
                        name: "FK_alarms_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alarm_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    alarm_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    value = table.Column<double>(type: "REAL", nullable: true),
                    actor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarm_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_alarm_events_alarms_alarm_id",
                        column: x => x.alarm_id,
                        principalTable: "alarms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alarm_events_alarm_at",
                table: "alarm_events",
                columns: new[] { "alarm_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_alarms_project_raised",
                table: "alarms",
                columns: new[] { "project_id", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alarms_project_state",
                table: "alarms",
                columns: new[] { "project_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_alarms_tag_raised",
                table: "alarms",
                columns: new[] { "tag_name", "raised_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alarm_events");

            migrationBuilder.DropTable(
                name: "alarms");
        }
    }
}
