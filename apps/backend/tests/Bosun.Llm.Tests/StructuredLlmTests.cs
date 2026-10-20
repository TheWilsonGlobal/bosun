using System.Net;
using System.Text.Json.Nodes;
using Bosun.Application.Llm;

namespace Bosun.Llm.Tests;

public sealed record Weather(string City, double TempC, WeatherKind Kind, string? Note = null);

public enum WeatherKind
{
    Sunny,
    Rainy,
}

public class StructuredJsonTests
{
    [Fact]
    public void The_schema_is_generated_from_the_record_with_required_members_and_enum_names()
    {
        var schema = StructuredJson.SchemaFor<Weather>();

        schema["type"]!.GetValue<string>().Should().Be("object");
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        required.Should().BeEquivalentTo("city", "tempC", "kind");
        schema["properties"]!["kind"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("sunny", "rainy");
        schema["properties"]!["note"]!["type"]!.ToJsonString().Should().Contain("null");
    }

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("  {\"a\":1}  ", "{\"a\":1}")]
    public void Markdown_fences_are_stripped(string raw, string expected) => StructuredJson.StripFence(raw).Should().Be(expected);
}

public class StructuredLlmTests
{
    private const string Good = "{\"city\":\"Hanoi\",\"tempC\":31.5,\"kind\":\"sunny\",\"note\":null}";

    private static LlmRequest Request() => new() { Feature = "test", Messages = [LlmMessage.User("weather?")] };

    [Fact]
    public async Task Valid_json_is_parsed_into_the_type_on_the_first_call()
    {
        var llm = new ScriptedLlm().Reply(Good);

        var result = await new StructuredLlm(llm).GenerateAsync<Weather>(Request());

        result.Value.Should().Be(new Weather("Hanoi", 31.5, WeatherKind.Sunny, null));
        result.ExtraCalls.Should().Be(0);
        llm.Requests.Single().ResponseSchema!.Name.Should().Be("Weather");
        llm.Requests.Single().MaxOutputTokens.Should().Be(1000);
    }

    [Fact]
    public async Task A_code_fenced_reply_and_different_member_casing_are_accepted_without_a_repair()
    {
        var llm = new ScriptedLlm().Reply("```json\n{\"City\":\"Hue\",\"TempC\":20,\"Kind\":\"rainy\"}\n```");

        var result = await new StructuredLlm(llm).GenerateAsync<Weather>(Request());

        result.Value.City.Should().Be("Hue");
        llm.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Malformed_json_gets_exactly_one_repair_turn_that_shows_the_model_its_own_reply()
    {
        var llm = new ScriptedLlm().Reply("{\"city\": \"Hanoi\", \"tempC\":").Reply(Good);

        var result = await new StructuredLlm(llm).GenerateAsync<Weather>(Request());

        result.Value.City.Should().Be("Hanoi");
        result.ExtraCalls.Should().Be(1);
        var repair = llm.Requests[1].Messages;
        repair.Should().HaveCount(3);
        repair[1].Role.Should().Be(LlmRole.Model);
        ((TextPart)repair[1].Parts[0]).Text.Should().Contain("\"tempC\":");
        ((TextPart)repair[2].Parts[0]).Text.Should().Contain("could not be used").And.Contain("ONLY a JSON");
        result.Usage.TotalTokens.Should().Be(30, "both calls are counted");
    }

    [Fact]
    public async Task A_missing_required_member_or_a_wrong_enum_is_malformed_and_repaired()
    {
        var llm = new ScriptedLlm().Reply("{\"city\":\"Hanoi\",\"kind\":\"sunny\"}").Reply("{\"city\":\"Hanoi\",\"tempC\":1,\"kind\":\"foggy\"}");

        var act = async () => await new StructuredLlm(llm).GenerateAsync<Weather>(Request());

        var failure = (await act.Should().ThrowAsync<LlmException>()).Which;
        failure.Code.Should().Be(LlmErrorCodes.MalformedOutput);
        failure.Message.Should().Contain("repair");
        llm.Requests.Should().HaveCount(2, "one repair, then give up");
    }

    [Fact]
    public async Task A_numeric_enum_is_not_accepted()
    {
        var llm = new ScriptedLlm().Reply("{\"city\":\"Hanoi\",\"tempC\":1,\"kind\":0}").Reply(Good);
        var result = await new StructuredLlm(llm).GenerateAsync<Weather>(Request());
        result.ExtraCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_semantic_check_failure_triggers_the_same_single_repair()
    {
        var llm = new ScriptedLlm().Reply(Good).Reply("{\"city\":\"Hue\",\"tempC\":20,\"kind\":\"rainy\"}");

        var result = await new StructuredLlm(llm).GenerateAsync<Weather>(Request(), w => w.City == "Hue" ? null : "the city must be Hue.");

        result.Value.City.Should().Be("Hue");
        ((TextPart)llm.Requests[1].Messages[^1].Parts[0]).Text.Should().Contain("the city must be Hue.");
    }

    [Fact]
    public async Task An_empty_reply_and_json_null_are_repaired_too()
    {
        var llm = new ScriptedLlm().Reply("").Reply("null");
        var act = async () => await new StructuredLlm(llm).GenerateAsync<Weather>(Request());
        (await act.Should().ThrowAsync<LlmException>()).Which.Code.Should().Be(LlmErrorCodes.MalformedOutput);
    }

    [Fact]
    public async Task A_truncated_answer_is_retried_with_a_doubled_budget()
    {
        var llm = new ScriptedLlm().Reply("{\"city\":\"Ha", LlmFinishReason.MaxTokens, thoughts: 990).Reply(Good);

        var result = await new StructuredLlm(llm).GenerateAsync<Weather>(Request());

        result.Value.City.Should().Be("Hanoi");
        llm.Requests.Select(r => r.MaxOutputTokens).Should().Equal(1000, 2000);
        llm.Requests[1].Messages.Should().HaveCount(1, "a truncation retry is the same prompt, not a repair turn");
    }

    [Fact]
    public async Task Truncation_keeps_escalating_up_to_the_cap_and_then_is_llm_truncated()
    {
        var llm = new ScriptedLlm()
            .Reply("{", LlmFinishReason.MaxTokens, thoughts: 900)
            .Reply("{", LlmFinishReason.MaxTokens, thoughts: 1900)
            .Reply("{", LlmFinishReason.MaxTokens, thoughts: 3900);

        var act = async () => await new StructuredLlm(llm).GenerateAsync<Weather>(Request());

        var failure = (await act.Should().ThrowAsync<LlmException>()).Which;
        failure.Code.Should().Be(LlmErrorCodes.Truncated);
        failure.Message.Should().Contain("4000").And.Contain("reasoning");
        llm.Requests.Select(r => r.MaxOutputTokens).Should().Equal(1000, 2000, 4000);
    }

    [Fact]
    public async Task Text_calls_recover_from_truncation_the_same_way()
    {
        var llm = new ScriptedLlm().Reply("The sea is sal", LlmFinishReason.MaxTokens).Reply("The sea is salty because of dissolved minerals.");

        var response = await new StructuredLlm(llm).GenerateTextAsync(Request());

        response.Text.Should().EndWith("minerals.");
        llm.Requests.Select(r => r.MaxOutputTokens).Should().Equal(1000, 2000);

        var stuck = new ScriptedLlm().Reply("x", LlmFinishReason.MaxTokens).Reply("x", LlmFinishReason.MaxTokens).Reply("x", LlmFinishReason.MaxTokens);
        var act = async () => await new StructuredLlm(stuck).GenerateTextAsync(Request());
        (await act.Should().ThrowAsync<LlmException>()).Which.Code.Should().Be(LlmErrorCodes.Truncated);
    }

    [Fact]
    public async Task Provider_errors_pass_through_untouched()
    {
        using var host = new LlmHost(new FakeHandler().Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "API key not valid. Please pass a valid API key.", ("API_KEY_INVALID", null))));

        var act = async () => await host.Structured.GenerateAsync<Weather>(Request());

        (await act.Should().ThrowAsync<LlmException>()).Which.Code.Should().Be(LlmErrorCodes.AuthFailed);
    }

    [Fact]
    public async Task End_to_end_through_the_real_adapter_a_truncation_then_a_repair_costs_three_logged_calls()
    {
        var handler = new FakeHandler()
            .ThenOk("{\"city\":\"Ha", prompt: 20, candidates: 4, thoughts: 8000, finish: "MAX_TOKENS")
            .ThenOk("not json at all", prompt: 20, candidates: 4)
            .ThenOk(Good, prompt: 30, candidates: 20);
        using var host = new LlmHost(handler);

        var result = await host.Structured.GenerateAsync<Weather>(Request() with { PromptVersion = "weather@abc12345" });

        result.Value.City.Should().Be("Hanoi");
        result.Calls.Should().HaveCount(3);
        result.Usage.PromptTokens.Should().Be(70);
        result.EstimatedCostUsd.Should().BeGreaterThan(0);
        handler.Requests[0].Json["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().Should().Be(8192);
        handler.Requests[1].Json["generationConfig"]!["maxOutputTokens"]!.GetValue<int>().Should().Be(16384);
        host.Log.Calls.Should().HaveCount(3).And.OnlyContain(c => c.PromptVersion == "weather@abc12345" && c.Feature == "test");
        host.Log.Calls[0].ThoughtTokens.Should().Be(8000);
    }

    [Fact]
    public async Task Tool_declarations_can_be_combined_with_a_schema_request()
    {
        var llm = new ScriptedLlm().Reply(Good);
        var tool = new LlmToolDeclaration("noop", "does nothing", new JsonObject { ["type"] = "object" });

        await new StructuredLlm(llm).GenerateAsync<Weather>(Request() with { Tools = [tool] });

        llm.Requests.Single().Tools.Should().ContainSingle();
    }
}
