using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bosun.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vessels",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    imo_number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vessels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    vessel_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.ForeignKey(
                        name: "FK_projects_vessels_vessel_id",
                        column: x => x.vessel_id,
                        principalTable: "vessels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alarm_groups",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarm_groups", x => x.id);
                    table.ForeignKey(
                        name: "FK_alarm_groups_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    actor_type = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    actor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    approved_by = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    agent_run_id = table.Column<int>(type: "INTEGER", nullable: true),
                    change_set_id = table.Column<int>(type: "INTEGER", nullable: true),
                    tag_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    op_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    diff_json = table.Column<string>(type: "TEXT", nullable: false),
                    timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_entries_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "change_sets",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    created_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ops = table.Column<string>(type: "TEXT", nullable: false),
                    requires_second_approver = table.Column<bool>(type: "INTEGER", nullable: false),
                    approved_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    approved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    second_approved_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    second_approved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    applied_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    applied_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    rejected_by = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    rejected_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    reject_reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    agent_run_id = table.Column<int>(type: "INTEGER", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_sets", x => x.id);
                    table.ForeignKey(
                        name: "FK_change_sets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "controllers",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: true),
                    ip_address = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_controllers", x => x.id);
                    table.ForeignKey(
                        name: "FK_controllers_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "io_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    controller_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    signal_type = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_io_modules", x => x.id);
                    table.ForeignKey(
                        name: "FK_io_modules_controllers_controller_id",
                        column: x => x.controller_id,
                        principalTable: "controllers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channels",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    io_module_id = table.Column<int>(type: "INTEGER", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    signal_type = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channels", x => x.id);
                    table.ForeignKey(
                        name: "FK_channels_io_modules_io_module_id",
                        column: x => x.io_module_id,
                        principalTable: "io_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    project_id = table.Column<int>(type: "INTEGER", nullable: false),
                    tag_name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    system = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    equipment = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    signal_type = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    unit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    range_low = table.Column<double>(type: "REAL", nullable: true),
                    range_high = table.Column<double>(type: "REAL", nullable: true),
                    safety_class = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    alarm_group_id = table.Column<int>(type: "INTEGER", nullable: true),
                    channel_id = table.Column<int>(type: "INTEGER", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.id);
                    table.ForeignKey(
                        name: "FK_tags_alarm_groups_alarm_group_id",
                        column: x => x.alarm_group_id,
                        principalTable: "alarm_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tags_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tags_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alarm_limits",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    tag_id = table.Column<int>(type: "INTEGER", nullable: false),
                    level = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    value = table.Column<double>(type: "REAL", nullable: false),
                    delay_s = table.Column<double>(type: "REAL", nullable: false),
                    hysteresis = table.Column<double>(type: "REAL", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarm_limits", x => x.id);
                    table.ForeignKey(
                        name: "FK_alarm_limits_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_alarm_groups_project_name",
                table: "alarm_groups",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_alarm_limits_tag_level",
                table: "alarm_limits",
                columns: new[] { "tag_id", "level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_project_change_set",
                table: "audit_entries",
                columns: new[] { "project_id", "change_set_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_project_tag",
                table: "audit_entries",
                columns: new[] { "project_id", "tag_name" });

            migrationBuilder.CreateIndex(
                name: "ix_change_sets_project_status",
                table: "change_sets",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_channels_module_number",
                table: "channels",
                columns: new[] { "io_module_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_controllers_project_name",
                table: "controllers",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_io_modules_controller_slot",
                table: "io_modules",
                columns: new[] { "controller_id", "slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_vessel_id",
                table: "projects",
                column: "vessel_id");

            migrationBuilder.CreateIndex(
                name: "ix_tags_alarm_group",
                table: "tags",
                column: "alarm_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_tags_project_system",
                table: "tags",
                columns: new[] { "project_id", "system" });

            migrationBuilder.CreateIndex(
                name: "ux_tags_channel",
                table: "tags",
                column: "channel_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tags_project_tag_name",
                table: "tags",
                columns: new[] { "project_id", "tag_name" },
                unique: true);

            // ---- FTS5 trigram search over tag name + description (plan s5.1) -------------------------------
            // Raw SQL on purpose: EF cannot model virtual tables. External-content table: the index stores no copy of
            // the text, and the triggers below keep it in step with `tags`. NOTE for later migrations: SQLite rebuilds a
            // table on some ALTERs, which silently drops its triggers; any migration that rebuilds `tags` must
            // recreate tags_fts_ai / tags_fts_ad / tags_fts_au and run the 'rebuild' command again.
            migrationBuilder.Sql(
                "CREATE VIRTUAL TABLE tag_fts USING fts5(tag_name, description, content='tags', content_rowid='id', tokenize='trigram');");
            migrationBuilder.Sql(
                @"CREATE TRIGGER tags_fts_ai AFTER INSERT ON tags BEGIN
                    INSERT INTO tag_fts(rowid, tag_name, description) VALUES (new.id, new.tag_name, new.description);
                  END;");
            migrationBuilder.Sql(
                @"CREATE TRIGGER tags_fts_ad AFTER DELETE ON tags BEGIN
                    INSERT INTO tag_fts(tag_fts, rowid, tag_name, description) VALUES ('delete', old.id, old.tag_name, old.description);
                  END;");
            migrationBuilder.Sql(
                @"CREATE TRIGGER tags_fts_au AFTER UPDATE OF tag_name, description ON tags BEGIN
                    INSERT INTO tag_fts(tag_fts, rowid, tag_name, description) VALUES ('delete', old.id, old.tag_name, old.description);
                    INSERT INTO tag_fts(rowid, tag_name, description) VALUES (new.id, new.tag_name, new.description);
                  END;");
            migrationBuilder.Sql("INSERT INTO tag_fts(tag_fts) VALUES ('rebuild');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tags_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tags_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tags_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS tag_fts;");

            migrationBuilder.DropTable(
                name: "alarm_limits");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "change_sets");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "alarm_groups");

            migrationBuilder.DropTable(
                name: "channels");

            migrationBuilder.DropTable(
                name: "io_modules");

            migrationBuilder.DropTable(
                name: "controllers");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "vessels");
        }
    }
}
