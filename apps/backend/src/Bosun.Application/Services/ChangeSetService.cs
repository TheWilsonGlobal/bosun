using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Bosun.Rules;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Application;

/// <summary>Serialises all change-set writes inside this process (SQLite has one writer; this also avoids lost updates).</summary>
public sealed class WriteGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>
/// The change-set use cases: draft, validate, approve (with SIL/ESD second approver), apply, reject, diff.
/// Humans and the agent use the same path; only <see cref="ApplyAsync"/> mutates tags and it needs a human
/// <see cref="UserContext"/> (plan s7.1).
/// </summary>
public sealed class ChangeSetService(IBosunDbContext db, WriteGate gate, TimeProvider clock, Monitoring.ConfigChangeNotifier changes)
{
    public const int MaxTitleLength = 200;

    /// <summary>Keeps (page - 1) * pageSize far below int.MaxValue.</summary>
    public const int MaxPage = 1_000_000;

    // ---------- reads ----------

    public async Task<PagedResult<ChangeSetSummaryDto>> ListAsync(int projectId, ChangeSetStatus? status, int page, int pageSize, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        if (page is < 1 or > MaxPage || pageSize is < 1 or > 200)
        {
            throw AppException.Invalid($"page must be between 1 and {MaxPage} and pageSize between 1 and 200.");
        }

        var query = db.ChangeSets.AsNoTracking().Where(c => c.ProjectId == projectId);
        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<ChangeSetSummaryDto>(rows.Select(Summary).ToList(), total, page, pageSize);
    }

    public async Task<ChangeSetDto> GetAsync(int id, CancellationToken ct) => ToDto(await LoadAsync(id, tracked: false, ct));

    public async Task<IReadOnlyList<AuditEntryDto>> AuditAsync(int projectId, int? changeSetId, string? tagName, int take, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);
        var query = db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (changeSetId is { } cs)
        {
            query = query.Where(a => a.ChangeSetId == cs);
        }

        if (!string.IsNullOrWhiteSpace(tagName))
        {
            query = query.Where(a => a.TagName == tagName);
        }

        var rows = await query.OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return rows.Select(a => new AuditEntryDto(
            a.Id, a.ProjectId, a.ActorType, a.ProposedBy, a.Actor, a.ApprovedBy, a.Model, a.PromptVersion, a.AgentRunId, a.ChangeSetId,
            a.TagName, a.OpType, JsonNode.Parse(a.DiffJson), a.Timestamp)).ToList();
    }

    public async Task<DiffDto> DiffAsync(int id, CancellationToken ct)
    {
        var cs = await LoadAsync(id, tracked: false, ct);
        var rows = cs.Ops.Select((op, i) => DiffRow(i, op)).ToList();
        if (cs.Status is ChangeSetStatus.Applied or ChangeSetStatus.Rejected)
        {
            // Rules compare against the *current* project; for a finished change set that is meaningless.
            return new DiffDto(cs.Id, cs.Status, cs.RequiresSecondApprover, rows, []);
        }

        var eval = await EvaluateAsync(cs, ct);
        return new DiffDto(cs.Id, cs.Status, eval.RequiresSecondApprover, rows, eval.Violations.Select(ToDto).ToList());
    }

    // ---------- writes ----------

    public async Task<ChangeSetDto> CreateAsync(int projectId, CreateChangeSetRequest request, UserContext user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        return await CreateWhileHoldingGateAsync(projectId, request, user, ct);
    }

    /// <summary>
    /// <see cref="CreateAsync"/> for a caller that ALREADY holds the <see cref="WriteGate"/> (for example one that wraps several creates in
    /// one transaction): the gate is not re-entered, so the transaction and the gate are held for exactly the same span.
    /// </summary>
    public async Task<ChangeSetDto> CreateWhileHoldingGateAsync(int projectId, CreateChangeSetRequest request, UserContext user, CancellationToken ct)
    {
        await EnsureProjectAsync(projectId, ct);

        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength)
        {
            throw AppException.Invalid($"title is required and at most {MaxTitleLength} characters.");
        }

        var cs = new ChangeSet
        {
            ProjectId = projectId,
            Title = title,
            Source = request.Source,
            Status = ChangeSetStatus.Draft,
            CreatedBy = user.Name,
            CreatedAt = Now(),
            Model = request.Model,
            PromptVersion = request.PromptVersion,
            AgentRunId = request.AgentRunId,
        };

        if (request.Ops.Count > 0)
        {
            cs.Ops = await ResolveAsync(cs, request.Ops, ct);
            cs.RequiresSecondApprover = await WouldRequireSecondApproverAsync(cs, ct);
        }

        db.ChangeSets.Add(cs);
        await SaveAsync(ct);
        return ToDto(cs);
    }

    public async Task<ChangeSetDto> AddOpsAsync(int id, IReadOnlyList<OpInput> ops, UserContext user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var cs = await LoadAsync(id, tracked: true, ct);
        if (cs.Status is ChangeSetStatus.Applied or ChangeSetStatus.Rejected)
        {
            throw AppException.InvalidState($"A {cs.Status.ToString().ToLowerInvariant()} change set cannot be edited.", cs.Status);
        }

        if (ops.Count == 0)
        {
            throw AppException.Invalid("ops must contain at least one operation.");
        }

        cs.Ops = [.. cs.Ops, .. await ResolveAsync(cs, ops, ct)];
        ResetApprovals(cs);
        cs.RequiresSecondApprover = await WouldRequireSecondApproverAsync(cs, ct);
        await SaveAsync(ct);
        return ToDto(cs);
    }

    /// <summary>The identity the agent writes under. It holds no roles, so it can never be a second approver.</summary>
    public static UserContext AgentUser { get; } = new("agent", new HashSet<string>());

    /// <summary>
    /// Creates the empty DRAFT an agent run fills. This is the only way to create a change set with source=agent: the HTTP
    /// create endpoint refuses that source, so provenance (model, prompt version, run id) can only be set server-side.
    /// </summary>
    public Task<ChangeSetDto> CreateAgentDraftAsync(int projectId, string title, int agentRunId, string? model, string? promptVersion, CancellationToken ct) =>
        CreateAsync(projectId, new CreateChangeSetRequest(title, ChangeSetSource.Agent, [], model, promptVersion, agentRunId), AgentUser, ct);

    /// <summary>
    /// Agent path of <see cref="AddOpsAsync"/>: the new ops are simulated first and, if any of them is wrong on its own
    /// (unknown tag, unknown group, stale value ...), NONE is added and the violations come back so the model can repair
    /// the call. Tag-level rules (limit order, ranges ...) are judged later, by validation of the whole draft.
    /// </summary>
    public async Task<AddOpsOutcome> TryAddOpsAsync(int id, IReadOnlyList<OpInput> ops, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var cs = await LoadAsync(id, tracked: true, ct);
        if (cs.Status is ChangeSetStatus.Applied or ChangeSetStatus.Rejected)
        {
            throw AppException.InvalidState($"A {cs.Status.ToString().ToLowerInvariant()} change set cannot be edited.", cs.Status);
        }

        if (ops.Count == 0)
        {
            throw AppException.Invalid("ops must contain at least one operation.");
        }

        var resolved = await ResolveAsync(cs, ops, ct);
        var view = await PreloadedProjectView.LoadAsync(db, cs.ProjectId, cs.Ops.Concat(resolved), ct);
        var simulator = new ChangeSetSimulator(view);
        foreach (var existing in cs.Ops)
        {
            simulator.Apply(existing);
        }

        var errors = new List<ViolationDto>();
        foreach (var op in resolved)
        {
            errors.AddRange(simulator.Apply(op).Where(v => v.Severity == Severity.Error).Select(ToDto));
        }

        if (errors.Count > 0)
        {
            return new AddOpsOutcome(null, errors);
        }

        cs.Ops = [.. cs.Ops, .. resolved];
        ResetApprovals(cs);
        cs.RequiresSecondApprover = await WouldRequireSecondApproverAsync(cs, ct);
        await SaveAsync(ct);
        return new AddOpsOutcome(ToDto(cs), []);
    }

    /// <summary>The rule check of a change set without changing its status (what the agent's validate_draft tool runs).</summary>
    public async Task<ValidationResultDto> CheckAsync(int id, CancellationToken ct)
    {
        var cs = await LoadAsync(id, tracked: false, ct);
        var eval = await EvaluateAsync(cs, ct);
        return new ValidationResultDto(cs.Id, cs.Status, !eval.HasErrors, eval.RequiresSecondApprover, eval.Violations.Select(ToDto).ToList());
    }

    public async Task<ValidationResultDto> ValidateAsync(int id, UserContext user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var cs = await LoadAsync(id, tracked: true, ct);
        if (cs.Status is ChangeSetStatus.Applied or ChangeSetStatus.Rejected)
        {
            throw AppException.InvalidState($"A {cs.Status.ToString().ToLowerInvariant()} change set cannot be validated.", cs.Status);
        }

        var eval = await EvaluateAsync(cs, ct);
        if (eval.HasErrors)
        {
            ResetApprovals(cs);
        }
        else if (cs.Status == ChangeSetStatus.Draft)
        {
            cs.Status = ChangeSetStatus.Validated;
        }
        else if (cs.Status == ChangeSetStatus.Approved && eval.RequiresSecondApprover && cs.SecondApprovedBy is null)
        {
            // Approved as an ordinary change set, but the project moved on and it now touches a SIL/ESD tag: it is no
            // longer approved. The first approval stays on record; a different approver completes it (otherwise it
            // would be stuck: Approve needs "validated" and Apply would answer 403 forever).
            cs.Status = ChangeSetStatus.Validated;
        }

        cs.RequiresSecondApprover = eval.RequiresSecondApprover;
        await SaveAsync(ct);
        return new ValidationResultDto(cs.Id, cs.Status, !eval.HasErrors, eval.RequiresSecondApprover, eval.Violations.Select(ToDto).ToList());
    }

    /// <summary>
    /// First call records the approval. A SIL/ESD change set additionally needs a second call from a different
    /// user who holds the approver role; only then does it become <see cref="ChangeSetStatus.Approved"/>.
    /// </summary>
    public async Task<ChangeSetDto> ApproveAsync(int id, UserContext user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var cs = await LoadAsync(id, tracked: true, ct);
        if (cs.Status != ChangeSetStatus.Validated)
        {
            throw AppException.InvalidState(
                $"Only a validated change set can be approved (this one is {cs.Status.ToString().ToLowerInvariant()}). Validate it first.", cs.Status);
        }

        var eval = await EvaluateAsync(cs, ct);
        if (eval.HasErrors)
        {
            ResetApprovals(cs);
            await SaveAsync(ct);
            throw ValidationFailed(eval);
        }

        cs.RequiresSecondApprover = eval.RequiresSecondApprover;
        if (cs.ApprovedBy is null)
        {
            cs.ApprovedBy = user.Name;
            cs.ApprovedAt = Now();
            if (!cs.RequiresSecondApprover)
            {
                cs.Status = ChangeSetStatus.Approved;
            }
        }
        else
        {
            if (string.Equals(cs.ApprovedBy, user.Name, StringComparison.Ordinal))
            {
                throw new AppException(ErrorKind.Forbidden, "second_approver_required",
                    "This change set touches SIL/ESD tags: the second approval must come from a different user.",
                    new Dictionary<string, object?> { ["reason"] = "same_user", ["firstApprover"] = cs.ApprovedBy });
            }

            if (!user.IsApprover)
            {
                throw new AppException(ErrorKind.Forbidden, "approver_role_required",
                    "The second approver of a SIL/ESD change set must hold the approver role.",
                    new Dictionary<string, object?> { ["role"] = UserContext.ApproverRole });
            }

            cs.SecondApprovedBy = user.Name;
            cs.SecondApprovedAt = Now();
            cs.Status = ChangeSetStatus.Approved;
        }

        await SaveAsync(ct);
        return ToDto(cs);
    }

    public async Task<ChangeSetDto> RejectAsync(int id, string? reason, UserContext user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var cs = await LoadAsync(id, tracked: true, ct);
        if (cs.Status is ChangeSetStatus.Applied or ChangeSetStatus.Rejected)
        {
            throw AppException.InvalidState($"A {cs.Status.ToString().ToLowerInvariant()} change set cannot be rejected.", cs.Status);
        }

        cs.Status = ChangeSetStatus.Rejected;
        cs.RejectedBy = user.Name;
        cs.RejectedAt = Now();
        cs.RejectReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
        await SaveAsync(ct);
        return ToDto(cs);
    }

    /// <summary>
    /// One transaction: re-validate against the current project, check every op's <c>old</c> still matches
    /// (otherwise 409), write the tag changes, one <see cref="AuditEntry"/> per op, bump versions, mark applied.
    /// </summary>
    public async Task<ChangeSetDto> ApplyAsync(int id, UserContext user, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(ct);
        var cs = await LoadAsync(id, tracked: true, ct);
        if (cs.Status != ChangeSetStatus.Approved)
        {
            throw AppException.InvalidState(
                $"Only an approved change set can be applied (this one is {cs.Status.ToString().ToLowerInvariant()}).", cs.Status);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var eval = await EvaluateAsync(cs, ct);

        if (eval.Conflicts.Count > 0)
        {
            throw new AppException(ErrorKind.Conflict, "concurrency_conflict",
                "The project changed since this change set was drafted: one or more operations no longer match the current values.",
                new Dictionary<string, object?> { ["conflicts"] = eval.Conflicts.Select(ToDto).ToList() });
        }

        if (eval.HasErrors)
        {
            throw ValidationFailed(eval);
        }

        if (eval.RequiresSecondApprover && (cs.SecondApprovedBy is null || cs.ApprovedBy is null))
        {
            throw new AppException(ErrorKind.Forbidden, "second_approver_required",
                "This change set touches SIL/ESD tags and has not been approved by a second approver.",
                new Dictionary<string, object?> { ["reason"] = "missing" });
        }

        await MaterializeAsync(cs, eval, ct);

        var now = Now();
        var approvers = string.Join(", ", new[] { cs.ApprovedBy, cs.SecondApprovedBy }.Where(a => a is not null));
        for (var i = 0; i < cs.Ops.Count; i++)
        {
            var op = cs.Ops[i];
            db.AuditEntries.Add(new AuditEntry
            {
                ProjectId = cs.ProjectId,
                // The actor is always the human who applied it; agent provenance rides in ProposedBy + Model/PromptVersion/AgentRunId.
                ActorType = ActorType.User,
                ProposedBy = cs.Source == ChangeSetSource.Agent ? ActorType.Agent : ActorType.User,
                Actor = user.Name,
                ApprovedBy = approvers,
                Model = cs.Model,
                PromptVersion = cs.PromptVersion,
                AgentRunId = cs.AgentRunId,
                ChangeSetId = cs.Id,
                TagName = op.TagName,
                OpType = op.TypeName,
                DiffJson = JsonSerializer.Serialize(op, BosunJson.Options),
                Timestamp = now,
            });
        }

        cs.Status = ChangeSetStatus.Applied;
        cs.AppliedBy = user.Name;
        cs.AppliedAt = now;
        cs.RequiresSecondApprover = eval.RequiresSecondApprover;

        await SaveAsync(ct);
        await tx.CommitAsync(ct);

        // Committed: tell the live alarm engine to re-read the limits (it never reads uncommitted data, and a failure here is not ours to report).
        changes.NotifyApplied(cs.ProjectId);
        return ToDto(cs);
    }

    // ---------- internals ----------

    /// <summary>Applies the simulated final state of each touched tag to the tracked entities.</summary>
    private async Task MaterializeAsync(ChangeSet cs, EvaluationResult eval, CancellationToken ct)
    {
        var names = eval.TouchedTags.ToList();
        var entities = await db.Tags.Include(t => t.AlarmLimits)
            .Where(t => t.ProjectId == cs.ProjectId && names.Contains(t.TagName))
            .ToDictionaryAsync(t => t.TagName, StringComparer.Ordinal, ct);
        var groups = await db.AlarmGroups.Where(g => g.ProjectId == cs.ProjectId).ToDictionaryAsync(g => g.Name, g => g.Id, StringComparer.Ordinal, ct);

        // A channel that moves from one tag to another inside this change set would transiently violate the unique
        // index on tags.channel_id (statement order is not dependency-ordered). Release those first, in an earlier save.
        var acquired = eval.FinalState.Values.Where(f => f?.ChannelId is not null).Select(f => f!.ChannelId!.Value).ToHashSet();
        var released = false;
        foreach (var name in names)
        {
            if (entities.TryGetValue(name, out var entity) && entity.ChannelId is { } held
                && eval.FinalState[name]?.ChannelId != held && acquired.Contains(held))
            {
                entity.ChannelId = null;
                released = true;
            }
        }

        if (released)
        {
            await SaveAsync(ct);
        }

        foreach (var name in names)
        {
            var final = eval.FinalState[name];
            entities.TryGetValue(name, out var entity);

            if (final is null)
            {
                if (entity is not null)
                {
                    db.Tags.Remove(entity);
                }

                continue;
            }

            if (entity is null)
            {
                var parts = TagRules.ParseName(name)!.Value; // the name convention is an error-level rule, so it parses here
                entity = new Tag { ProjectId = cs.ProjectId, TagName = name, System = parts.System, Equipment = parts.Equipment };
                db.Tags.Add(entity);
                Copy(entity, final, groups);
                continue;
            }

            if (Copy(entity, final, groups))
            {
                // Limits-only changes leave the tag row itself "Unchanged"; touching Version forces the update + version bump.
                db.Entry(entity).Property(t => t.Version).IsModified = true;
            }
        }
    }

    /// <summary>Copies a snapshot onto an entity. Returns true when anything differs.</summary>
    private bool Copy(Tag entity, TagSnapshot final, Dictionary<string, int> groups)
    {
        var changed = false;

        void Set<T>(T current, T next, Action<T> assign)
        {
            if (!EqualityComparer<T>.Default.Equals(current, next))
            {
                assign(next);
                changed = true;
            }
        }

        Set(entity.Description, final.Description, v => entity.Description = v);
        Set(entity.SignalType, final.SignalType, v => entity.SignalType = v);
        Set(entity.Unit, final.Unit, v => entity.Unit = v);
        Set(entity.RangeLow, final.RangeLow, v => entity.RangeLow = v);
        Set(entity.RangeHigh, final.RangeHigh, v => entity.RangeHigh = v);
        Set(entity.SafetyClass, final.SafetyClass, v => entity.SafetyClass = v);
        Set(entity.ChannelId, final.ChannelId, v => entity.ChannelId = v);
        int? groupId = final.AlarmGroup is { } g ? groups[g] : null;
        Set(entity.AlarmGroupId, groupId, v => entity.AlarmGroupId = v);

        foreach (var existing in entity.AlarmLimits.ToList())
        {
            if (!final.Limits.TryGetValue(existing.Level, out var wanted))
            {
                entity.AlarmLimits.Remove(existing);
                db.AlarmLimits.Remove(existing);
                changed = true;
            }
            else if (existing.Value != wanted.Value || existing.DelayS != wanted.DelayS || existing.Hysteresis != wanted.Hysteresis)
            {
                existing.Value = wanted.Value;
                existing.DelayS = wanted.DelayS;
                existing.Hysteresis = wanted.Hysteresis;
                changed = true;
            }
        }

        foreach (var (level, wanted) in final.Limits)
        {
            if (entity.AlarmLimits.All(l => l.Level != level))
            {
                entity.AlarmLimits.Add(new AlarmLimit { Level = level, Value = wanted.Value, DelayS = wanted.DelayS, Hysteresis = wanted.Hysteresis });
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Fills in omitted <c>old</c> values from the state the earlier ops leave, and enforces the op cap.</summary>
    private async Task<List<ChangeOp>> ResolveAsync(ChangeSet cs, IReadOnlyList<OpInput> inputs, CancellationToken ct)
    {
        if (cs.Ops.Count + inputs.Count > RuleOptions.Default.MaxOps)
        {
            throw new AppException(ErrorKind.Unprocessable, "too_many_ops",
                $"A change set may contain at most {RuleOptions.Default.MaxOps} operations.",
                new Dictionary<string, object?> { ["max"] = RuleOptions.Default.MaxOps, ["current"] = cs.Ops.Count, ["adding"] = inputs.Count });
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            // The JSON boundary already refuses these; this protects in-process callers (agent, extraction) from persisting one.
            if (ChangeSetSimulator.UndefinedEnum(inputs[i].Op) is { } bad)
            {
                throw AppException.Invalid($"ops[{i}].{bad.Field} has the undefined value {bad.Value}.", new Dictionary<string, object?> { ["index"] = i });
            }
        }

        var view = await PreloadedProjectView.LoadAsync(db, cs.ProjectId, cs.Ops.Concat(inputs.Select(i => i.Op)), ct);
        var simulator = new ChangeSetSimulator(view);
        foreach (var existing in cs.Ops)
        {
            simulator.Apply(existing);
        }

        var resolved = new List<ChangeOp>(inputs.Count);
        foreach (var input in inputs)
        {
            var op = input.OldProvided ? input.Op : OldValueResolver.Fill(input.Op, simulator.Current(input.Op.TagName));
            simulator.Apply(op);
            resolved.Add(op);
        }

        return resolved;
    }

    /// <summary>Early warning for the UI: does this draft touch SIL/ESD tags? (Re-checked authoritatively at validate/approve/apply.)</summary>
    private async Task<bool> WouldRequireSecondApproverAsync(ChangeSet cs, CancellationToken ct) =>
        (await EvaluateAsync(cs, ct)).RequiresSecondApprover;

    private async Task<EvaluationResult> EvaluateAsync(ChangeSet cs, CancellationToken ct)
    {
        var view = await PreloadedProjectView.LoadAsync(db, cs.ProjectId, cs.Ops, ct);
        return ChangeSetEvaluator.Evaluate(cs.Ops, view);
    }

    private static void ResetApprovals(ChangeSet cs)
    {
        cs.Status = ChangeSetStatus.Draft;
        cs.RequiresSecondApprover = false;
        cs.ApprovedBy = null;
        cs.ApprovedAt = null;
        cs.SecondApprovedBy = null;
        cs.SecondApprovedAt = null;
    }

    private async Task<ChangeSet> LoadAsync(int id, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.ChangeSets : db.ChangeSets.AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound("Change set", id);
    }

    private async Task EnsureProjectAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppException(ErrorKind.Conflict, "concurrent_modification",
                "Another change was saved at the same time. Reload and try again.");
        }
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static AppException ValidationFailed(EvaluationResult eval) =>
        new(ErrorKind.Unprocessable, "validation_failed", "The change set has validation errors.",
            new Dictionary<string, object?> { ["violations"] = eval.Violations.Select(ToDto).ToList() });

    public static ViolationDto ToDto(RuleViolation v) => new(v.Code, v.Severity, v.TagName, v.Field, v.Message);

    private static ChangeSetSummaryDto Summary(ChangeSet c) => new(
        c.Id, c.ProjectId, c.Title, c.Source, c.Status, c.CreatedBy, c.CreatedAt, c.Ops.Count, c.RequiresSecondApprover,
        c.ApprovedBy, c.SecondApprovedBy, c.AppliedAt, c.Version);

    public static ChangeSetDto ToDto(ChangeSet c) => new(
        c.Id, c.ProjectId, c.Title, c.Source, c.Status, c.CreatedBy, c.CreatedAt, c.Ops, c.RequiresSecondApprover,
        c.RequiresSecondApprover && c.Status == ChangeSetStatus.Validated && c.ApprovedBy is not null,
        c.ApprovedBy, c.ApprovedAt, c.SecondApprovedBy, c.SecondApprovedAt, c.AppliedBy, c.AppliedAt,
        c.RejectedBy, c.RejectedAt, c.RejectReason, c.Model, c.PromptVersion, c.AgentRunId, c.Version);

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Limit(AlarmLimitValue? v) => v is null ? "(none)" : $"{Num(v.Value)} (delay {Num(v.DelayS)} s, hyst {Num(v.Hysteresis)})";

    private static DiffRowDto DiffRow(int index, ChangeOp op) => op switch
    {
        CreateTagOp c => new DiffRowDto(index, op.TypeName, op.TagName, null, null,
            $"{c.SignalType} {c.Unit} {(c.RangeLow is null ? "" : Num(c.RangeLow.Value))}..{(c.RangeHigh is null ? "" : Num(c.RangeHigh.Value))}".Trim(), op),
        UpdateTagOp u => new DiffRowDto(index, op.TypeName, op.TagName, u.Field, u.Old, u.New, op),
        SetAlarmLimitOp s => new DiffRowDto(index, op.TypeName, op.TagName, $"limits.{s.Level}", Limit(s.Old), Limit(s.New), op),
        AssignGroupOp a => new DiffRowDto(index, op.TypeName, op.TagName, "alarmGroup", a.Old, a.New, op),
        BindChannelOp b => new DiffRowDto(index, op.TypeName, op.TagName, "channelId", b.Old?.ToString(CultureInfo.InvariantCulture), b.New?.ToString(CultureInfo.InvariantCulture), op),
        DeleteTagOp d => new DiffRowDto(index, op.TypeName, op.TagName, null, d.Old is null ? null : $"{d.Old.SignalType} {d.Old.Unit}".Trim(), null, op),
        _ => new DiffRowDto(index, op.TypeName, op.TagName, null, null, null, op),
    };
}
