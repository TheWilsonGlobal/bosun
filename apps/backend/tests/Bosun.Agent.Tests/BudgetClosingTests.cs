using System.Text.Json.Nodes;
using Bosun.Agent;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Tools;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Agent.Tests;

/// <summary>
/// Wall-clock budget behaviour (plan s7.4): the clock counts ACT time only, has a generous default, and a run whose only
/// missing piece is the closing call ends in review instead of being thrown away. Result kinds and cancel codes.
/// </summary>
public sealed class BudgetClosingTests
{
    private const string Demo = "Put ME-EXH-TT01 into alarm group ME-EXH.";

    /// <summary>A model call that does not answer within any test budget (it only ends when the run cancels it).</summary>
    private static ScriptedAgentLlm Stalls(ScriptedAgentLlm llm) => llm.Then(async (_, ct) =>
    {
        await Task.Delay(TimeSpan.FromSeconds(60), ct);
        return ScriptedAgentLlm.Respond([new TextPart("late")]);
    });

    [Fact]
    public void The_default_wall_clock_is_120_seconds_and_configurable()
    {
        new AgentOptions().ActWallClockSeconds.Should().Be(120);
        using var host = new AgentHost(new ScriptedAgentLlm(), new() { ["Agent:ActWallClockSeconds"] = "7" });
        host.Get<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.ActWallClockSeconds.Should().Be(7);
    }

    [Fact]
    public async Task The_plan_call_does_not_use_up_the_act_clock()
    {
        var llm = new ScriptedAgentLlm();
        using var host = new AgentHost(llm, new() { ["Agent:ActWallClockSeconds"] = "1.5" });
        llm.Then(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct); // longer than the whole ACT budget
                var plan = new JsonObject
                {
                    ["decision"] = "proceed",
                    ["message"] = "",
                    ["title"] = "t",
                    ["steps"] = new JsonArray("a"),
                    ["affected_scope"] = "s",
                    ["risks"] = new JsonArray(),
                };
                return ScriptedAgentLlm.Respond([new TextPart(plan.ToJsonString())]);
            })
            .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01"], "ME-EXH"))
            .Finish("draft_ready", "Done.");

        var run = await host.RunAsync(Demo);

        run.Status.Should().Be(AgentRunStatus.AwaitingReview);
        run.ErrorCode.Should().BeNull();
        run.ElapsedMs.Should().BeGreaterThan(1500, "the run as a whole took longer than the ACT budget");
    }

    [Fact]
    public async Task A_complete_clean_draft_whose_closing_call_is_cut_off_ends_in_review_with_a_note()
    {
        var llm = new ScriptedAgentLlm();
        using var host = new AgentHost(llm, new() { ["Agent:ActWallClockSeconds"] = "0.6" });
        llm.Plan().Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01", "ME-EXH-TT02"], "ME-EXH"));
        Stalls(llm); // the closing / validate call never arrives in time

        var run = await host.RunAsync(Demo);

        run.Status.Should().Be(AgentRunStatus.AwaitingReview);
        run.ResultKind.Should().Be("draft");
        run.ErrorCode.Should().BeNull();
        run.ResultMessage.Should().Contain("2 operation").And.Contain("closing summary");
        run.Steps![^1].Payload!["note"]!.GetValue<string>().Should().Be(AgentErrorCodes.BudgetWallClock);
        var cs = await host.GetChangeSetAsync(run.ChangeSetId!.Value);
        cs.Ops.Should().HaveCount(2);
        cs.Status.Should().Be(ChangeSetStatus.Validated, "a clean draft is validated like on the normal path");
        cs.ApprovedBy.Should().BeNull();
        host.Events.Done.Should().ContainSingle(d => d.Status == AgentRunStatus.AwaitingReview);
    }

    [Fact]
    public async Task A_stall_after_validate_draft_is_also_just_the_closing_call()
    {
        var llm = new ScriptedAgentLlm();
        using var host = new AgentHost(llm, new() { ["Agent:ActWallClockSeconds"] = "0.6" });
        llm.Plan()
            .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01"], "ME-EXH"))
            .Call(ToolNames.ValidateDraft, new JsonObject());
        Stalls(llm);

        var run = await host.RunAsync(Demo);

        run.Status.Should().Be(AgentRunStatus.AwaitingReview);
    }

    [Fact]
    public async Task Genuinely_unfinished_work_stays_budget_wall_clock()
    {
        // 1. nothing drafted yet
        var empty = new ScriptedAgentLlm();
        using (var host = new AgentHost(empty, new() { ["Agent:ActWallClockSeconds"] = "0.4" }))
        {
            Stalls(empty.Plan());
            var run = await host.RunAsync(Demo);
            run.Status.Should().Be(AgentRunStatus.BudgetExceeded);
            run.ErrorCode.Should().Be(AgentErrorCodes.BudgetWallClock);
            run.ResultKind.Should().Be("budget_exceeded");
        }

        // 2. the model was still searching for more targets after its last write
        var searching = new ScriptedAgentLlm();
        using (var host = new AgentHost(searching, new() { ["Agent:ActWallClockSeconds"] = "0.6" }))
        {
            searching.Plan()
                .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01"], "ME-EXH"))
                .Call(ToolNames.SearchTags, Args.Obj(("query", Args.V("exhaust"))));
            Stalls(searching);
            var run = await host.RunAsync(Demo);
            run.Status.Should().Be(AgentRunStatus.BudgetExceeded);
            run.ErrorCode.Should().Be(AgentErrorCodes.BudgetWallClock);
            run.ChangeSetId.Should().NotBeNull("the partial draft is kept");
        }

        // 3. the last tool call failed: the model was in the middle of repairing it
        var failing = new ScriptedAgentLlm();
        using (var host = new AgentHost(failing, new() { ["Agent:ActWallClockSeconds"] = "0.6" }))
        {
            failing.Plan()
                .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01"], "ME-EXH"))
                .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT02"], "NO-SUCH-GROUP"));
            Stalls(failing);
            var run = await host.RunAsync(Demo);
            run.Status.Should().Be(AgentRunStatus.BudgetExceeded);
            run.ErrorCode.Should().Be(AgentErrorCodes.BudgetWallClock);
        }
    }

    [Fact]
    public async Task A_draft_with_rule_errors_is_not_passed_off_as_complete_when_time_runs_out()
    {
        var llm = new ScriptedAgentLlm();
        using var host = new AgentHost(llm, new() { ["Agent:ActWallClockSeconds"] = "0.6" });

        // Two highs at the same level are fine per op; an H above the existing HH is only caught by the whole-draft check.
        var tag = await ErrorProneTagAsync(host);
        llm.Plan().Call(ToolNames.ProposeSetAlarmLimit, Args.SetLimit([tag.Name], "H", tag.Hh + 10, 5));
        Stalls(llm);

        var run = await host.RunAsync($"Raise the high alarm of {tag.Name}");

        run.Status.Should().Be(AgentRunStatus.BudgetExceeded);
        run.ErrorCode.Should().Be(AgentErrorCodes.BudgetWallClock);
    }

    [Fact]
    public async Task Budget_exceeded_and_cancelled_runs_carry_a_result_kind()
    {
        var llm = new ScriptedAgentLlm();
        using var host = new AgentHost(llm, new() { ["Agent:MaxToolCalls"] = "1" });
        llm.Plan()
            .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT01"], "ME-EXH"))
            .Call(ToolNames.ProposeAssignGroup, Args.Assign(["ME-EXH-TT02"], "ME-EXH"));
        var budget = await host.RunAsync(Demo);
        budget.Status.Should().Be(AgentRunStatus.BudgetExceeded);
        budget.ResultKind.Should().Be("budget_exceeded");

        var cancelLlm = new ScriptedAgentLlm();
        using var cancelHost = new AgentHost(cancelLlm);
        var started = new TaskCompletionSource();
        cancelLlm.Plan().Then(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ScriptedAgentLlm.Respond([]);
        });
        var manager = cancelHost.Get<AgentRunManager>();
        var id = await manager.StartAsync(1, Demo, AgentHost.Engineer, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        manager.Cancel(id).Should().BeTrue();
        await manager.Completion(id)!.WaitAsync(TimeSpan.FromSeconds(30));
        var run = await cancelHost.GetRunAsync(id);
        run.Status.Should().Be(AgentRunStatus.Cancelled);
        run.ResultKind.Should().Be("cancelled");
    }

    [Fact]
    public async Task Cancel_distinguishes_a_finished_run_from_one_the_server_does_not_hold()
    {
        var llm = new ScriptedAgentLlm();
        using var host = new AgentHost(llm);
        llm.Plan(PlanDecision.Refuse, "No.");
        var finished = await host.RunAsync(Demo);

        int orphanId;
        await using (var scope = host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
            var orphan = new AgentRun { ProjectId = 1, Message = "orphan", Status = AgentRunStatus.Running, CreatedBy = "alice", StartedAt = DateTime.UtcNow };
            db.AgentRuns.Add(orphan);
            await db.SaveChangesAsync();
            orphanId = orphan.Id;
        }

        await using var q = host.Scope();
        var queries = q.ServiceProvider.GetRequiredService<AgentQueries>();

        var done = await ((Func<Task>)(() => queries.CancelAsync(finished.Id, CancellationToken.None))).Should().ThrowAsync<AppException>();
        done.Which.Code.Should().Be("run_already_finished");
        done.Which.Kind.Should().Be(ErrorKind.Conflict);

        var gone = await ((Func<Task>)(() => queries.CancelAsync(orphanId, CancellationToken.None))).Should().ThrowAsync<AppException>();
        gone.Which.Code.Should().Be("run_not_active");
        gone.Which.Kind.Should().Be(ErrorKind.Conflict);
    }

    private static async Task<(string Name, double Hh)> ErrorProneTagAsync(AgentHost host)
    {
        await using var scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var tag = await db.Tags.AsNoTracking().Include(t => t.AlarmLimits)
            .Where(t => t.AlarmLimits.Any(l => l.Level == AlarmLevel.H) && t.AlarmLimits.Any(l => l.Level == AlarmLevel.HH))
            .OrderBy(t => t.TagName).FirstAsync();
        return (tag.TagName, tag.AlarmLimits.Single(l => l.Level == AlarmLevel.HH).Value);
    }
}
