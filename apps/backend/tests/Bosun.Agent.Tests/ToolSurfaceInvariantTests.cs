using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Tools;
using Bosun.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Agent.Tests;

/// <summary>
/// Plan s7.1: "There is deliberately no apply tool." These tests pin that down from four sides: the names in the registry, what the
/// tools can reach (constructor dependencies, the draft interface, the compiled call graph), and what actually happens to the
/// tags when every tool is used.
/// </summary>
public sealed partial class ToolSurfaceInvariantTests : IDisposable
{
    private readonly AgentHost host = new(new ScriptedAgentLlm());

    public void Dispose() => host.Dispose();

    [GeneratedRegex("apply|approve|reject|delete|destroy|drop|wipe|commit|publish|exec|sql|shell|import|truncate|purge", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    private IReadOnlyList<IAgentTool> Tools()
    {
        var scope = host.Scope();
        return scope.ServiceProvider.GetRequiredService<ToolRegistry>().Tools;
    }

    [Fact]
    public void No_tool_in_the_registry_or_the_declarations_is_named_like_apply_approve_reject_or_delete()
    {
        var tools = Tools();
        tools.Should().NotBeEmpty();
        tools.Select(t => t.Name).Should().OnlyContain(n => !Forbidden().IsMatch(n));

        using var scope = host.Scope().ServiceProvider.CreateScope();
        var declarations = scope.ServiceProvider.GetRequiredService<ToolRegistry>().Declarations().Append(FinishTask.Declaration);
        declarations.Select(d => d.Name).Should().OnlyContain(n => !Forbidden().IsMatch(n));

        // Exactly the documented catalogue: a new tool must be added here on purpose.
        tools.Select(t => t.Name).Should().BeEquivalentTo(
        [
            "search_tags", "get_tags", "list_alarm_groups", "list_controllers", "search_docs",
            "propose_create_tag", "propose_update_tag", "propose_set_alarm_limit", "propose_assign_group",
            "validate_draft", "show_draft_summary",
        ]);
    }

    [Fact]
    public void Write_tools_are_proposals_and_nothing_else_writes()
    {
        var tools = Tools();
        tools.Where(t => t.Kind == ToolKind.WriteDraft).Select(t => t.Name).Should().OnlyContain(n => n.StartsWith("propose_", StringComparison.Ordinal));
        tools.Where(t => t.Name.StartsWith("propose_", StringComparison.Ordinal)).Should().OnlyContain(t => t.Kind == ToolKind.WriteDraft);
        Enum.GetValues<ToolKind>().Should().BeEquivalentTo([ToolKind.Read, ToolKind.WriteDraft], "there is no third, 'apply' kind");
    }

    [Fact]
    public void Tools_depend_only_on_read_services_and_the_draft_interface_exposes_no_apply()
    {
        var allowed = new[] { typeof(TagQueries), typeof(ProjectQueries), typeof(Bosun.Application.Rag.KnowledgeSearchService) };
        foreach (var tool in Tools())
        {
            var parameters = tool.GetType().GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
            parameters.Should().NotContain(p => !allowed.Contains(p), $"{tool.Name} may only read through the query services");
        }

        typeof(IAgentDraft).GetMembers().Select(m => m.Name).Should().OnlyContain(n => !Forbidden().IsMatch(n));
        typeof(ToolContext).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["ProjectId", "Draft"]);
    }

    [Fact]
    public void No_compiled_code_of_the_agent_or_its_tools_can_call_apply_approve_or_reject()
    {
        var forbidden = typeof(ChangeSetService).GetMethods()
            .Where(m => m.Name is nameof(ChangeSetService.ApplyAsync) or nameof(ChangeSetService.ApproveAsync) or nameof(ChangeSetService.RejectAsync))
            .ToHashSet();
        forbidden.Should().HaveCount(3);

        var assemblies = new[] { typeof(AgentRunner).Assembly, typeof(ToolRegistry).Assembly };
        var offenders = new List<string>();
        var seen = new HashSet<string>();
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.Assembly == typeof(AgentRunner).Assembly || t.Namespace == typeof(ToolRegistry).Namespace))
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                         .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                foreach (var callee in CalledMethods(method))
                {
                    seen.Add($"{callee.DeclaringType?.Name}.{callee.Name}");
                    if (callee is MethodInfo info && forbidden.Contains(info))
                    {
                        offenders.Add($"{type.FullName}.{method.Name} -> {info.Name}");
                    }
                }
            }
        }

        seen.Should().Contain("ChangeSetService.ValidateAsync", "the scan must really see calls, or an empty result proves nothing");
        offenders.Should().BeEmpty("only a human session may apply, approve or reject (plan s7.1)");
    }

    /// <summary>The PUBLIC ChangeSetService methods the tool surface can reach, by name (tools -> IAgentDraft -> its implementation -> service), three calls deep.</summary>
    public static IReadOnlyList<string> ChangeSetMethodsReachableFromTools(out IReadOnlyList<Type> toolTypes, out int reachableCount)
    {
        var assemblies = CallGraph.BosunAssemblies(typeof(AgentRunner).Assembly, typeof(ToolRegistry).Assembly);
        toolTypes = assemblies.SelectMany(a => a.GetTypes()).Where(t => !t.IsAbstract && !t.IsInterface && typeof(IAgentTool).IsAssignableFrom(t)).ToList();
        var drafts = assemblies.SelectMany(a => a.GetTypes()).Where(t => !t.IsAbstract && !t.IsInterface && typeof(IAgentDraft).IsAssignableFrom(t));
        var roots = toolTypes.Concat(drafts).SelectMany(CallGraph.AllMethods);
        var reachable = CallGraph.Reachable(roots, 3, assemblies);
        reachableCount = reachable.Count;
        return reachable.Where(m => m.DeclaringType == typeof(ChangeSetService) && m.IsPublic && !m.IsSpecialName).Select(m => m.Name).Distinct().Order().ToList();
    }

    [Fact]
    public void Every_tool_implementation_in_every_loaded_bosun_assembly_cannot_reach_apply_approve_or_reject_three_calls_deep()
    {
        var reached = ChangeSetMethodsReachableFromTools(out var toolTypes, out var count);

        // The scan must see what it is looking for: every registered tool, the interface call into the draft, and the service behind it.
        toolTypes.Select(t => t.Name).Should().Contain(["SearchTagsTool", "GetTagsTool", "SearchDocsTool", "ProposeSetAlarmLimitTool", "ProposeCreateTagTool", "ValidateDraftTool"]);
        Tools().Select(t => t.GetType()).Should().BeSubsetOf(toolTypes, "a registered tool the scan did not cover would be unchecked");
        count.Should().BeGreaterThan(50, "the scan followed calls into the services, not just the tool bodies");
        reached.Should().Contain(["TryAddOpsAsync", "CreateAgentDraftAsync", "CheckAsync"], "the draft path is the one the tools use");

        reached.Should().NotContain([nameof(ChangeSetService.ApplyAsync), nameof(ChangeSetService.ApproveAsync), nameof(ChangeSetService.RejectAsync)]);
        reached.Should().BeSubsetOf(
            [
                nameof(ChangeSetService.CreateAgentDraftAsync), nameof(ChangeSetService.CreateWhileHoldingGateAsync), nameof(ChangeSetService.CreateAsync), nameof(ChangeSetService.TryAddOpsAsync),
                nameof(ChangeSetService.CheckAsync), nameof(ChangeSetService.GetAsync), nameof(ChangeSetService.ToDto),
            ],
            "a new service method reachable from the tools must be reviewed and added here on purpose");
    }

    [Fact]
    public async Task Using_every_tool_changes_no_tag_limit_group_or_audit_entry_and_leaves_the_change_set_a_draft()
    {
        var before = await host.TagFingerprintAsync();
        var changeSets = host.Get<IServiceScopeFactory>();
        await using var scope = changeSets.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ChangeSetService>();
        var registry = scope.ServiceProvider.GetRequiredService<ToolRegistry>();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var run = new Bosun.Domain.Entities.AgentRun { ProjectId = 1, Message = "surface", CreatedBy = "t", StartedAt = DateTime.UtcNow };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        var draft = new AgentDraftSession(service, 1, run.Id, "surface test", "m", "p");
        var context = new ToolContext(1, draft);

        var tags = new[] { "ME-EXH-TT01", "ME-EXH-TT02" };
        var calls = new (string Name, JsonObject Args)[]
        {
            (ToolNames.SearchTags, Args.Obj(("query", Args.V("ME-EXH")))),
            (ToolNames.GetTags, Args.Obj(("tag_names", Args.Names(tags)))),
            (ToolNames.ListAlarmGroups, new JsonObject()),
            (ToolNames.ListControllers, new JsonObject()),
            (ToolNames.ProposeSetAlarmLimit, Args.SetLimit(tags, "H", 95, 10)),
            (ToolNames.ProposeAssignGroup, Args.Assign(tags, "ME-EXH")),
            (ToolNames.ProposeUpdateTag, Args.Obj(("tag_names", Args.Names(tags)), ("field", Args.V("description")), ("value", Args.V("changed")))),
            (ToolNames.ProposeCreateTag, Args.Obj(("tag_name", Args.V("ME-EXH-TT09")), ("signal_type", Args.V("AI")), ("unit", Args.V("°C")), ("range_low", Args.V(0)), ("range_high", Args.V(600)))),
            (ToolNames.ValidateDraft, new JsonObject()),
            (ToolNames.ShowDraftSummary, new JsonObject()),
        };
        foreach (var (name, args) in calls)
        {
            var result = await registry.ExecuteAsync(name, args, context, CancellationToken.None);
            result.Ok.Should().BeTrue($"{name}: {result.Response}");
        }

        // search_docs is a read tool too; the seeded test database has no knowledge base, which it reports as a tool error (not an exception).
        var docs = await registry.ExecuteAsync(ToolNames.SearchDocs, Args.Obj(("question", Args.V("why is the exhaust gas temperature delay 10 s?"))), context, CancellationToken.None);
        docs.Ok.Should().BeFalse();
        docs.ErrorCode.Should().Be(ToolErrorCodes.NotIndexed);

        (await host.TagFingerprintAsync()).Should().Be(before);
        var cs = await service.GetAsync(draft.ChangeSetId!.Value, CancellationToken.None);
        cs.Status.Should().Be(ChangeSetStatus.Draft);
        cs.Source.Should().Be(ChangeSetSource.Agent);
        cs.Ops.Should().HaveCount(2 + 2 + 2 + 1);
        await using var check = host.Scope();
        check.ServiceProvider.GetRequiredService<IBosunDbContext>().AuditEntries.Should().BeEmpty();
    }

    // ---------- IL scan ----------

    private static readonly Dictionary<short, OpCode> OpCodeTable = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        var module = method.Module;
        var i = 0;
        while (i < il.Length)
        {
            short code = il[i++];
            if (code == 0xFE)
            {
                code = (short)(0xFE00 | il[i++]);
            }

            var op = OpCodeTable[code];
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, i) * 4),
                _ => 4,
            };

            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? callee = null;
                try
                {
                    callee = module.ResolveMethod(BitConverter.ToInt32(il, i), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or BadImageFormatException)
                {
                    // a token that cannot be resolved in this context cannot be one of the three forbidden methods
                }

                if (callee is not null)
                {
                    yield return callee;
                }
            }

            i += size;
        }
    }
}
