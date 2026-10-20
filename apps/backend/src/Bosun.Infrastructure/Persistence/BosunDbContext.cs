using System.Text.Json;
using Bosun.Application;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bosun.Infrastructure.Persistence;

/// <summary>
/// Provider-neutral model (plan s5.1): nothing in here is SQLite- or Npgsql-specific. Provider selection,
/// pragmas and migrations live in the provider folders / migration assemblies.
/// </summary>
public class BosunDbContext(DbContextOptions<BosunDbContext> options) : DbContext(options), IBosunDbContext
{
    public DbSet<Vessel> Vessels => Set<Vessel>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Controller> Controllers => Set<Controller>();
    public DbSet<IoModule> IoModules => Set<IoModule>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<AlarmGroup> AlarmGroups => Set<AlarmGroup>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AlarmLimit> AlarmLimits => Set<AlarmLimit>();
    public DbSet<ChangeSet> ChangeSets => Set<ChangeSet>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<EvalResult> EvalResults => Set<EvalResult>();
    public DbSet<SourceDocument> SourceDocuments => Set<SourceDocument>();
    public DbSet<Extraction> Extractions => Set<Extraction>();
    public DbSet<ExtractedRow> ExtractedRows => Set<ExtractedRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Timestamps are UTC DateTime (SQLite cannot ORDER BY DateTimeOffset). Re-stamp Kind=Utc on the way out.
        configuration.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configuration.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();

        // Enums are stored as readable strings: stable across reordering and easy to query by hand.
        configuration.Properties<SignalType>().HaveConversion<string>().HaveMaxLength(8);
        configuration.Properties<AlarmLevel>().HaveConversion<string>().HaveMaxLength(8);
        configuration.Properties<SafetyClass>().HaveConversion<string>().HaveMaxLength(8);
        configuration.Properties<ChangeSetStatus>().HaveConversion<string>().HaveMaxLength(16);
        configuration.Properties<ChangeSetSource>().HaveConversion<string>().HaveMaxLength(16);
        configuration.Properties<ActorType>().HaveConversion<string>().HaveMaxLength(8);
        configuration.Properties<AgentRunStatus>().HaveConversion<string>().HaveMaxLength(24);
        configuration.Properties<AgentStepKind>().HaveConversion<string>().HaveMaxLength(16);
        configuration.Properties<ExtractionStatus>().HaveConversion<string>().HaveMaxLength(16);
        configuration.Properties<ExtractedRowState>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Vessel>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.ImoNumber).HasMaxLength(20);
        });

        model.Entity<Project>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Vessel).WithMany(v => v.Projects).HasForeignKey(x => x.VesselId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Controllers).WithOne(c => c.Project).HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.AlarmGroups).WithOne(g => g.Project).HasForeignKey(g => g.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithOne(t => t.Project).HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Controller>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique().HasDatabaseName("ux_controllers_project_name");
        });

        model.Entity<IoModule>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Controller).WithMany(c => c.Modules).HasForeignKey(x => x.ControllerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ControllerId, x.Slot }).IsUnique().HasDatabaseName("ux_io_modules_controller_slot");
        });

        model.Entity<Channel>(e =>
        {
            e.HasOne(x => x.IoModule).WithMany(m => m.Channels).HasForeignKey(x => x.IoModuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IoModuleId, x.Number }).IsUnique().HasDatabaseName("ux_channels_module_number");
        });

        model.Entity<AlarmGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique().HasDatabaseName("ux_alarm_groups_project_name");
        });

        model.Entity<Tag>(e =>
        {
            e.Property(x => x.TagName).HasMaxLength(100).IsRequired();
            e.Property(x => x.System).HasMaxLength(20).IsRequired();
            e.Property(x => x.Equipment).HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.HasOne(x => x.AlarmGroup).WithMany().HasForeignKey(x => x.AlarmGroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Channel).WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.SetNull);

            // plan s5: unique (project_id, tag_name)
            e.HasIndex(x => new { x.ProjectId, x.TagName }).IsUnique().HasDatabaseName("ux_tags_project_tag_name");
            e.HasIndex(x => new { x.ProjectId, x.System }).HasDatabaseName("ix_tags_project_system");
            e.HasIndex(x => x.AlarmGroupId).HasDatabaseName("ix_tags_alarm_group");

            // a channel serves at most one tag (NULLs are distinct on both SQLite and PostgreSQL)
            e.HasIndex(x => x.ChannelId).IsUnique().HasDatabaseName("ux_tags_channel");
        });

        model.Entity<AlarmLimit>(e =>
        {
            e.HasOne(x => x.Tag).WithMany(t => t.AlarmLimits).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TagId, x.Level }).IsUnique().HasDatabaseName("ux_alarm_limits_tag_level");
        });

        model.Entity<ChangeSet>(e =>
        {
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApprovedBy).HasMaxLength(100);
            e.Property(x => x.SecondApprovedBy).HasMaxLength(100);
            e.Property(x => x.AppliedBy).HasMaxLength(100);
            e.Property(x => x.RejectedBy).HasMaxLength(100);
            e.Property(x => x.RejectReason).HasMaxLength(500);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.PromptVersion).HasMaxLength(100);

            // Provenance link: deleting a run (retention) must not delete the draft it produced.
            e.HasOne<AgentRun>().WithMany().HasForeignKey(x => x.AgentRunId).OnDelete(DeleteBehavior.SetNull);

            // Typed ops as one JSON document with a "type" discriminator (TEXT on SQLite; Phase 8 maps it to jsonb).
            // A value converter (not ToJson()) keeps the polymorphic record hierarchy in Bosun.Domain free of EF.
            e.Property(x => x.Ops)
                .HasConversion(
                    ops => JsonSerializer.Serialize(ops, BosunJson.Options),
                    json => JsonSerializer.Deserialize<List<ChangeOp>>(json, BosunJson.Options) ?? new List<ChangeOp>(),
                    new ValueComparer<List<ChangeOp>>(
                        (a, b) => JsonSerializer.Serialize(a, BosunJson.Options) == JsonSerializer.Serialize(b, BosunJson.Options),
                        v => JsonSerializer.Serialize(v, BosunJson.Options).GetHashCode(),
                        v => JsonSerializer.Deserialize<List<ChangeOp>>(JsonSerializer.Serialize(v, BosunJson.Options), BosunJson.Options)!))
                .IsRequired();

            e.HasIndex(x => new { x.ProjectId, x.Status }).HasDatabaseName("ix_change_sets_project_status");
        });

        model.Entity<AuditEntry>(e =>
        {
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Actor).HasMaxLength(100).IsRequired();
            e.Property(x => x.ApprovedBy).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.PromptVersion).HasMaxLength(100);
            e.Property(x => x.TagName).HasMaxLength(100);
            e.Property(x => x.OpType).HasMaxLength(32).IsRequired();
            e.Property(x => x.DiffJson).IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.ChangeSetId }).HasDatabaseName("ix_audit_project_change_set");
            e.HasIndex(x => new { x.ProjectId, x.TagName }).HasDatabaseName("ix_audit_project_tag");
        });

        model.Entity<LlmCall>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Feature).HasMaxLength(100).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
            e.Property(x => x.PromptVersion).HasMaxLength(100);
            e.Property(x => x.FinishReason).HasMaxLength(40);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.HasIndex(x => x.Timestamp).HasDatabaseName("ix_llm_calls_timestamp");
            e.HasIndex(x => new { x.Feature, x.Model }).HasDatabaseName("ix_llm_calls_feature_model");
        });

        model.Entity<AgentRun>(e =>
        {
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Message).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Phase).HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.PromptVersion).HasMaxLength(100);
            e.Property(x => x.ResultKind).HasMaxLength(24);
            e.Property(x => x.ResultMessage).HasMaxLength(2000);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.HasIndex(x => new { x.ProjectId, x.Status }).HasDatabaseName("ix_agent_runs_project_status");
            e.HasMany(x => x.Steps).WithOne(s => s.Run).HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AgentStep>(e =>
        {
            e.Property(x => x.PayloadJson).IsRequired();
            e.HasIndex(x => new { x.RunId, x.Seq }).IsUnique().HasDatabaseName("ux_agent_steps_run_seq");
        });

        model.Entity<EvalRun>(e =>
        {
            e.Property(x => x.Suite).HasMaxLength(32).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100).IsRequired();
            e.Property(x => x.PromptVersion).HasMaxLength(200).IsRequired();
            e.Property(x => x.ResultsFile).HasMaxLength(300);
            e.HasIndex(x => new { x.Suite, x.StartedAt }).HasDatabaseName("ix_eval_runs_suite_started");
            e.HasMany(x => x.Results).WithOne(r => r.EvalRun).HasForeignKey(r => r.EvalRunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<EvalResult>(e =>
        {
            e.Property(x => x.CaseId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Category).HasMaxLength(32).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.AgentStatus).HasMaxLength(24);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.HasIndex(x => new { x.EvalRunId, x.CaseId }).HasDatabaseName("ix_eval_results_run_case");
        });

        model.Entity<SourceDocument>(e =>
        {
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.StoredName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.UploadedBy).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.ProjectId).HasDatabaseName("ix_source_documents_project");
            e.HasIndex(x => x.StoredName).IsUnique().HasDatabaseName("ux_source_documents_stored_name");
            e.HasMany(x => x.Extractions).WithOne(x => x.SourceDocument).HasForeignKey(x => x.SourceDocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Extraction>(e =>
        {
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Phase).HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.PromptVersion).HasMaxLength(100);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.PagesJson).IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.Status }).HasDatabaseName("ix_extractions_project_status");
            e.HasMany(x => x.Rows).WithOne(r => r.Extraction).HasForeignKey(r => r.ExtractionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ExtractedRow>(e =>
        {
            e.Property(x => x.SourcePath).HasMaxLength(8).IsRequired();
            e.Property(x => x.SourceRef).HasMaxLength(200);
            e.Property(x => x.SourceSnippet).HasMaxLength(1000);
            e.Property(x => x.BboxJson).HasMaxLength(200);
            e.Property(x => x.ValuesJson).IsRequired();
            e.Property(x => x.RawJson).IsRequired();
            e.Property(x => x.FieldsJson).IsRequired();
            e.Property(x => x.FlagsJson).IsRequired();
            e.Property(x => x.ViolationsJson).IsRequired();
            e.Property(x => x.Band).HasMaxLength(8).IsRequired();
            e.HasIndex(x => new { x.ExtractionId, x.Seq }).IsUnique().HasDatabaseName("ux_extracted_rows_extraction_seq");
            e.HasIndex(x => new { x.ExtractionId, x.Band }).HasDatabaseName("ix_extracted_rows_extraction_band");
        });

        // Integer Version concurrency token on every aggregate (incremented in SaveChanges, below).
        foreach (var type in model.Model.GetEntityTypes().Where(t => typeof(VersionedEntity).IsAssignableFrom(t.ClrType) && t.ClrType != typeof(VersionedEntity)))
        {
            model.Entity(type.ClrType).Property(nameof(VersionedEntity.Version)).IsConcurrencyToken();
        }

        ApplySnakeCaseNames(model);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Added rows start at Version 1; every update moves Version to original + 1 (the WHERE clause uses the original).</summary>
    private void BumpVersions()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<VersionedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity.Version == 0:
                    entry.Entity.Version = 1;
                    break;
                case EntityState.Modified:
                    var version = entry.Property(e => e.Version);
                    version.CurrentValue = version.OriginalValue + 1;
                    break;
            }
        }
    }

    private static void ApplySnakeCaseNames(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } table)
            {
                entity.SetTableName(Snake(table));
            }

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.GetColumnName()));
            }
        }
    }

    private static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}

public sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

public sealed class NullableUtcDateTimeConverter()
    : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
