using System.Data.Common;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bosun.Infrastructure.Seed;

public static class DemoSeeder
{
    /// <summary>
    /// Inserts the generated vessel when the database holds no vessel yet, in ONE transaction with prepared, reused
    /// INSERT statements (about 13,000 rows). Plain <c>SaveChanges</c> needs ~5 s for the same rows (one statement per
    /// row plus change-tracking), which made <c>db:reset</c> and per-test seeding noticeably slow.
    /// Column names and value conversions come from the EF model, so this stays in step with the schema and the
    /// FTS5 triggers fire exactly as they do for normal writes. Returns the number of tags inserted (0 if already seeded).
    /// </summary>
    public static async Task<int> SeedIfEmptyAsync(BosunDbContext db, GeneratorOptions? options, CancellationToken ct = default)
    {
        if (await db.Vessels.AnyAsync(ct))
        {
            return 0;
        }

        return await InsertGraphAsync(db, VesselGenerator.Generate(options), ct);
    }

    /// <summary>Bulk-inserts a whole vessel graph into an EMPTY database (ids are assigned sequentially). Returns the tag count.</summary>
    public static async Task<int> InsertGraphAsync(BosunDbContext db, Vessel vessel, CancellationToken ct = default)
    {
        if (await db.Vessels.AnyAsync(ct))
        {
            throw new InvalidOperationException("The database already contains a vessel; bulk insert needs an empty database.");
        }

        var rows = Flatten(vessel);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await InsertAsync(db, rows.Vessels, ct);
        await InsertAsync(db, rows.Projects, ct);
        await InsertAsync(db, rows.AlarmGroups, ct);
        await InsertAsync(db, rows.Controllers, ct);
        await InsertAsync(db, rows.Modules, ct);
        await InsertAsync(db, rows.Channels, ct);
        await InsertAsync(db, rows.Tags, ct);
        await InsertAsync(db, rows.Limits, ct);
        await transaction.CommitAsync(ct);
        return rows.Tags.Count;
    }

    private sealed record Rows(
        List<Vessel> Vessels,
        List<Project> Projects,
        List<AlarmGroup> AlarmGroups,
        List<Controller> Controllers,
        List<IoModule> Modules,
        List<Channel> Channels,
        List<Tag> Tags,
        List<AlarmLimit> Limits);

    /// <summary>Assigns sequential ids and foreign keys (the database is empty) and lists rows parents-first.</summary>
    private static Rows Flatten(Vessel vessel)
    {
        var rows = new Rows([vessel], [], [], [], [], [], [], []);
        vessel.Id = 1;
        vessel.Version = 1;

        T Add<T>(List<T> list, T entity)
            where T : VersionedEntity
        {
            list.Add(entity);
            entity.Id = list.Count;
            entity.Version = 1;
            return entity;
        }

        foreach (var project in vessel.Projects)
        {
            Add(rows.Projects, project).VesselId = vessel.Id;

            foreach (var group in project.AlarmGroups)
            {
                Add(rows.AlarmGroups, group).ProjectId = project.Id;
            }

            foreach (var controller in project.Controllers)
            {
                Add(rows.Controllers, controller).ProjectId = project.Id;
                foreach (var module in controller.Modules)
                {
                    Add(rows.Modules, module).ControllerId = controller.Id;
                    foreach (var channel in module.Channels)
                    {
                        Add(rows.Channels, channel).IoModuleId = module.Id;
                    }
                }
            }

            foreach (var tag in project.Tags)
            {
                Add(rows.Tags, tag);
                tag.ProjectId = project.Id;
                tag.AlarmGroupId = tag.AlarmGroup?.Id;
                tag.ChannelId = tag.Channel?.Id;
                foreach (var limit in tag.AlarmLimits)
                {
                    Add(rows.Limits, limit).TagId = tag.Id;
                }
            }
        }

        return rows;
    }

    private static async Task InsertAsync<T>(BosunDbContext db, List<T> entities, CancellationToken ct)
        where T : class
    {
        if (entities.Count == 0)
        {
            return;
        }

        var entityType = db.Model.FindEntityType(typeof(T))!;
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var properties = entityType.GetProperties().Where(p => p.GetColumnName(table) is not null).ToList();

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            $"INSERT INTO \"{table.Name}\" ({string.Join(", ", properties.Select(p => $"\"{p.GetColumnName(table)}\""))}) " +
            $"VALUES ({string.Join(", ", properties.Select((_, i) => $"@p{i}"))})";

        var parameters = new DbParameter[properties.Count];
        for (var i = 0; i < properties.Count; i++)
        {
            parameters[i] = properties[i].GetRelationalTypeMapping().CreateParameter(command, $"@p{i}", null, properties[i].IsNullable);
            command.Parameters.Add(parameters[i]);
        }

        await command.PrepareAsync(ct);
        foreach (var entity in entities)
        {
            for (var i = 0; i < properties.Count; i++)
            {
                var value = properties[i].PropertyInfo!.GetValue(entity);
                var converter = properties[i].GetRelationalTypeMapping().Converter;
                if (value is not null && converter is not null)
                {
                    value = converter.ConvertToProvider(value);
                }

                parameters[i].Value = value ?? DBNull.Value;
            }

            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
